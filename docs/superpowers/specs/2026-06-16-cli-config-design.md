# PIN-Gated CLI Configuration — Design

Date: 2026-06-16
Status: Approved (design), pending implementation plan

## Goal

Let a parent set **all** Curfew settings non-interactively from the command line,
gated by the parent PIN — feature parity with the GUI Settings editor for
scripting/remote-admin use, without ever weakening the existing security model.

## Non-Goals

- No network/remote control channel. CLI runs **locally** on the protected machine
  (same as GUI); the existing local-only named-pipe IPC is the only write path.
- No bypass of passcode or lockout. CLI is exactly as authenticated as the GUI.
- No reinstall/uninstall scope. Config only.

## Surface

> **Revised during implementation:** originally planned as a `--config` mode inside
> `Curfew.App.exe`. That proved unworkable headless: `Curfew.App.exe` is a WinUI app
> whose Windows App SDK bootstrap initializer runs *before* `Main` and requires an
> interactive desktop, so it hangs when launched over SSH / in session 0 (verified on
> the Windows test VM). The CLI is therefore a **separate console-subsystem executable
> `curfew-cli.exe`** (`Curfew.Cli` project, references only `Curfew.Core`). Everything
> else below holds. The pure parser/validator lives in `Curfew.Core/Cli`.

A standalone console executable. Runs **fully headless**: real console I/O (no
`AttachConsole` needed), exits with a status code.

```
curfew-cli <command> [args] [--user <name|sid>] [--pin <pin>]
```

### Commands

Generic (mirrors IPC directly):
- `get <key>` — print current value (per-user scope applied if `--user` set)
- `set <key> <value>` — write any single config key

Typed helpers (validation + friendly errors, wrap the generic path):
- `set-limit <monday..sunday|all> <minutes>` — daily limit, clamp 0–1440
- `set-schedule <enabled|disabled> [grid]` — `schedule_enabled` + optional `schedule`
  grid (7×96 serialized form, validated via `Schedule.Parse`/`Serialize`)
- `set-timeout <minutes>` — `lock_screen_timeout`, clamp 1–720, stored ×60 as seconds
- `set-passcode <newPin>` — validate ≥8 chars, hash via `PasscodeHash.Hash`, write `passcode`

Introspection:
- `list-users` — print provisioned SIDs + resolved display names
- `--help` extended to document `--config`

### Modifiers

- `--user <name|sid>` — per-user scope. Accepts a Windows username (resolved to SID
  via `SecurityIdentifier`/`NTAccount.Translate`) or a raw SID. When set, reads/writes
  target `u:{sid}:{key}`. Rejected (exit 2) for device-wide-only keys
  (`SettingsPartition` global keys, e.g. `passcode`, `provisioned_users`).
- PIN input precedence: `stdin` → `CURFEW_PIN` env var → `--pin <pin>` arg.
  First non-empty source wins. Documented that `--pin` is visible in process list/
  history; stdin/env preferred for secure scripts.

## Data Flow

1. `Launch()` detects leading `--config`, routes to a new `CliConfig` handler
   (own class in `Curfew.App`, keeps `App.xaml.cs` thin).
2. Handler parses command + modifiers, resolves `--user` to SID if present.
3. Resolves PIN from stdin/env/arg.
4. Validates the command's value(s) **before** any write (clamp ranges, schedule
   shape, passcode length). Invalid → exit 2, nothing written.
5. Writes via `ConfigClient.SetConfig(key, value, pin)` (one call per key; `set-limit all`
   loops weekdays). Reads via a config read (per-user scope honored).
6. Maps IPC outcome to exit code and prints a one-line result.

`set-passcode` hashes locally then writes the `passcode` key through the same IPC
`set` op (the server already accepts a hashed value as the stored string; current
PIN still required to authorize the write — same as GUI `TrySavePasscode`).

## Validation (reused, not duplicated)

- Limits: `Math.Clamp(value, 0, 1440)` — reject out-of-range with exit 2 rather than
  silently clamp (CLI is explicit; surface the error).
- Timeout: clamp 1–720 minutes, store `×60` seconds.
- Passcode: `PasscodeHash.MinLength` (8). `PasscodeHash.Hash` for storage.
- Schedule: `Schedule.Parse` then `Serialize` to normalize/validate the grid.
- Key partition: `SettingsPartition.StoreFor(key)` must be Config; state keys rejected
  (server already enforces, CLI fails early with a clear message).

## Auth & Lockout

Every `set` carries the PIN to the SYSTEM `ConfigPipeServer`, which verifies via
`PasscodeHash.Verify` and enforces `LockoutPolicy` (3 free tries, exponential backoff
to 300s, recorded in `failed_attempts`/`failed_attempt_at`). CLI adds **no** new auth
surface and cannot bypass lockout. Bootstrap (no passcode set yet) passes through
exactly as the GUI first-run does — `set-passcode` is how the first PIN is set.

## Exit Codes

- `0` success
- `1` auth failed (wrong PIN) or locked out (message includes retry-after)
- `2` invalid arguments / value out of range / bad key / `--user` on global key
- `3` service unreachable (pipe not session-0 / not running)

## Components

- `Curfew.Core/Cli/CliCommand.cs` (new) — pure, cross-platform `CliCommandParser`:
  arg parsing, validation, PIN-source precedence. Unit-tested (no WinUI/Windows deps).
- `Curfew.Cli/Program.cs` (new) — console entry point: SID resolution, PIN resolution
  (bounded stdin read), config-pipe writes, console output, exit codes.
- `installer/build-installer.ps1` + `setup.iss` — publish + ship `curfew-cli.exe` as
  `{app}\app\curfew-cli.exe`.
- Reuses `Curfew.Core`: `ConfigClient`, `PasscodeHash`, `Schedule`, `SettingsStore`/
  `SettingsPartition`, `UserProvisioning`.

## Testing

xUnit in `tests/Curfew.Core.Tests` (existing style: isolated temp db per test).
- Move pure parse/validate logic into a testable unit (e.g. `CliConfigParser` in
  `Curfew.Core` or internal-visible in App) so tests cover:
  - arg parsing (command, `--user`, `--pin`, precedence stdin>env>arg)
  - value validation (limit ranges, timeout, passcode min length, schedule shape)
  - key partition rejection, `--user` on global key rejection
  - username→SID resolution boundary (mockable seam)
- IPC send path is thin; covered by existing server tests + manual smoke (service
  required). Document the manual smoke steps.

## Open Risks

- WinExe console attach: stdout only reliably reaches a parent console. For
  redirected/piped invocation (CI/SSH), confirm `AttachConsole` + redirected handles
  behave; fall back to writing a result line that callers can capture. Verify during
  implementation.
