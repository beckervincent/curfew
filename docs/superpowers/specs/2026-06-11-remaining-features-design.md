# Remaining features — design (per-user, C1, cat 3, cat 6)

Approved scope, built in dependency-ordered phases. Each phase ships + CI-greens
independently. The risky wiring (ACL flip, live IPC) is device-validated before a
release relies on it; the testable Core logic lands first.

## Decisions (from brainstorming)

- **Re-architecture depth:** *safe slice* — split the DB, write-protect config,
  keep per-user counters child-writable. Not the full service-owned-budget rewrite.
- **Per-user scope:** *per-child* limits + schedule (config keyed by SID) + a
  Settings user picker.
- **New user:** *locked until device code* (PBKDF2 in config.db) or parent passcode.
- **Cat 3:** reward time + wind-down + app-allowlist (all three).
- **Cat 6:** uninstall = admin-only + service self-heal. EV cert = future (needs paid cert).

## The two stores

| Store | ACL | Contents |
|-------|-----|----------|
| `config.db` | SYSTEM/Admins Full; **Users Read** | passcode, device_code, per-SID policy (limits/schedule/pause/warnings/idle/dns/timeguard/blocking_message/lock_timeout/unlock_secret/bonus), provisioned_users, failed-attempt counters, app_allowlist, schema_version, auto_update/channel |
| `state.db` | Users Modify | per-SID per-day `remaining_*`/`used_*`/`pause_used_*`/`session_active_*`, runtime `lock_*` coordination |

**Brick-safety:** Users can READ config.db, so the overlay/app verify the passcode
by a direct read; the pipe is only for WRITES. A pipe/service outage means "can't
save settings", never "can't unlock". Unlock path never touches the pipe.

## Risk solutions

1. **Brick-safety** — as above: config.db readable; verification is a direct read.
2. **Per-SID migration** — service, on boot, reads `schema_version` from config.db.
   If absent/old: copy the existing global policy keys to the console user's SID
   (`WTSGetActiveConsoleSessionId` → SID), set `provisioned_users=[sid]`, leave
   `device_code` unset (the existing passcode bootstraps), write `schema_version`.
   Idempotent; one-shot. Runs as SYSTEM (only writer of config.db).
3. **App-allowlist accuracy** — the per-second tick decrements the budget only when
   a **non-allowlisted process is foreground** (`GetForegroundWindow` → PID →
   image name). Idle and locked seconds already don't count. Heuristic by design:
   background allowlisted apps don't pause the clock — only the foreground one does.

## IPC (config writes)

- Named pipe `\\.\pipe\Curfew.Config`, `PipeSecurity` allowing Authenticated Users
  to connect; **authorization is per-op**, not per-connection.
- Line-delimited JSON `{ op, sid, key, value, passcode }` → `{ ok, error }`.
- Ops: `SetConfig` (passcode-gated), `Provision` (device-code or passcode-gated,
  adds a SID + seeds defaults), `RecordFailedAttempt` / `IsLockedOut`
  (service-owned backoff counter). The passcode/device-code is verified by the
  SYSTEM service against config.db — never trusted from a flag.

## Phases

- **P0 · Foundation** — `SettingsPartition` (pure key classifier: config vs state,
  per-SID vs global) + two-store `SettingsStore` + service migration + IPC server +
  App IPC client + installer ACLs. *Tested-first; ACL/IPC device-validated.*
- **P1 · Per-child config** — config keys per-SID; Settings user picker (enumerate
  provisioned SIDs → resolve display names via `LookupAccountSid`).
- **P2 · New-user lock + device code + lockout** — overlay locks an unprovisioned
  SID; lock "Activate this user" → device code / parent passcode → `Provision`.
  `LockoutPolicy` (pure: failed count + last time → locked + backoff) enforced by
  the service-owned counter.
- **P3 · Cat 3** — reward (one-off bonus via existing extend) · wind-down
  (configurable full-screen "N min left" before the hard lock) · app-allowlist
  (`ForegroundWatcher` in overlay + `AppAllowlist` match logic).
- **P4 · Cat 6** — service self-heal (boot re-register service + logon task if
  missing); document admin-only uninstall. EV cert = future.

## Testing

Pure logic (partition classifier, migration mapping, lockout policy, allowlist
match, wind-down timing) is unit-tested in Core. ACL, IPC and foreground tracking
are Windows-only and CI-compiled; they need on-device validation before a release
depends on them. The GDI-lock fallback and direct-read unlock bound the blast
radius throughout.
