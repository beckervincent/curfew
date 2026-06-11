# C1 — Settings write isolation (design)

## Problem

The settings/usage database `%ProgramData%\Curfew\data.db` is writable by
`BUILTIN\Users` (installer grants Modify). A non-admin child with any SQLite tool
can rewrite anything in it, which is the root cause behind most review findings:

- Overwrite the `passcode` row → set a known passcode → unlock the lock screen.
- Rewrite `schedule` / `schedule_enabled` → disable the curfew.
- Raise `limit_enabled` / the daily limit → grant more time.
- Zero `usage_<date>` / raise `remaining_<date>` → refill today's budget.
- Read `unlock_secret` (TOTP seed) → mint unlock codes; read the passcode hash →
  offline crack.

## Why this isn't a one-line ACL change

The obvious fix — make the DB read-only for Users and route writes through the
SYSTEM service — breaks the app, because **three processes write the DB and two
of them run as the logged-in (child) user**:

| Writer | Runs as | Keys it writes |
|--------|---------|----------------|
| `Curfew.App` (Settings/Setup) | logged-in user | all config keys, `passcode`, `unlock_secret`, `tray_command*` — already behind the passcode UI |
| `Curfew.Overlay` (lock/countdown) | logged-in user | `remaining_<date>`, `usage_<date>` (every tick), `unlock_last_counter`, `tray_command`=`""` (consume) |
| `Curfew.Service` (worker) | SYSTEM | usage/remaining reconciliation, updates |

The hard part is `remaining_<date>` / `usage_<date>`: the **overlay updates the
countdown every second**. If those writes go through an IPC the service accepts
from any local user, a child just calls `set remaining=99999` — the bypass moves
to the pipe with no security gained ("fake fix"). Truly protecting the budget
means the **SYSTEM service must own the countdown**, with the overlay demoted to a
display+request client. That is a real re-architecture, not an ACL tweak.

## Target architecture (full fix)

1. **Service owns the budget.** The worker maintains `remaining`/`usage` and the
   clock authority; the overlay renders them and requests actions
   (extend/pause/quit) over IPC, each gated by passcode where it grants time.
2. **Config is write-protected.** `passcode`, `unlock_secret`, `schedule`,
   limits, DNS/pause/time-guard/update settings live in a store the service
   writes; the App sends passcode-authenticated write requests over a named pipe.
3. **Secrets are not child-readable.** `passcode` hash and `unlock_secret` move to
   a SYSTEM-only store; the overlay verifies a passcode / redeems an unlock code
   by asking the service (`VerifyPasscode`, `RedeemUnlockCode`), never by reading
   the secret. This also gives the service the tamper-resistant failed-attempt
   lockout (C2).
4. **ACLs:** data dir `SYSTEM`/`Administrators` Full, `Users` Read; secret store
   `SYSTEM`/`Administrators` only.

### IPC

- Named pipe `\\.\pipe\Curfew.Control`, `PipeSecurity` allowing Authenticated
  Users to connect; **authorization is per-op, not per-connection.**
- Line-delimited JSON `{ op, key, value, passcode }` → `{ ok, error, value }`.
- Ops: `SetConfig` (passcode-gated), `VerifyPasscode`, `RedeemUnlockCode`,
  `RecordFailedAttempt`/`IsLockedOut`, `ConsumeTrayCommand` (clear-only, no auth),
  `BumpUnlockCounter` (monotonic-increase only).

## Achievable safe slice (no budget re-architecture)

If we want a real win before the full re-architecture, the safe subset is:

- **Write-protect config only.** A separate `config.db` (Users **read**, not
  write) holds `passcode`, `unlock_secret`, `schedule*`, limits and the rest of
  the settings; the App writes them via passcode-gated IPC. The overlay still
  **reads** the passcode hash directly to verify, so **unlock cannot break**
  (no brick risk).
- **Leave `remaining_/usage_` child-writable** in `state.db` (overlay countdown
  unaffected).
- Route the two sensitive overlay writes through IPC: `unlock_last_counter`
  (monotonic) and `tray_command` clear.

**Closes:** passcode reset, schedule/curfew disable, limit raise, settings
tamper. **Residual (documented):** a child can still refill *today's* usage
counter (daily-limit evasion) — but the **curfew schedule and the lock-screen
passcode stay intact** — and can still *read* the hash/seed (mitigated by the
8-char minimum; full close needs the SYSTEM-only secret store above).

**Risk:** config save now needs the service's pipe up; if it's down the App
surfaces an error (recoverable). No path here can lock the parent out, because
passcode verification keeps reading from a still-readable store.

## Recommendation

Do the **safe slice** now; schedule the **budget re-architecture** (service-owned
countdown + SYSTEM-only secret store + lockout) as its own pass with on-device
validation, since getting the countdown ownership wrong risks freezing the timer
or locking the user out of their own machine.
