# WinUI lock screen with maximal anti-bypass hardening (design)

## Goal

Replace the GDI Win32 lock UI (which "looks horrible") with a WinUI surface that
matches the app's design, **without** weakening enforcement — and add the
hardening that a same-session lock actually needs to resist bypass.

## Threat model reality

The lock runs in the **child's own session**. Neither the overlay nor a WinUI app
is a trust boundary — both run as the child. So a same-session lock can never be
*cryptographically* unbypassable; the real attack surface is **escaping the lock
UI**: killing the process, Alt+Tab / Win key, a second monitor, Task Manager.
(Forging the unlock flag via the child-writable DB is the separate C1 problem;
see `c1-settings-write-isolation.md`. It is not made worse by this change.)

## Architecture — robust core stays Win32, visuals go WinUI

| Layer | Process | Responsibility |
|-------|---------|----------------|
| Enforcement core | `Curfew.Overlay` (Win32) | tick + ShouldBlock; **instant fullscreen black cover**; **WH_KEYBOARD_LL** hook (swallow Win/Alt+Tab/Alt+F4/Ctrl+Esc); topmost reassert; launch + relaunch the WinUI lock; apply unlock actions |
| Lock UI | `Curfew.App --lock` (WinUI) | the pretty surface (primary card + per-monitor covers); passcode/extend/logoff input; writes the chosen action back |
| Hardening + watchdog | `Curfew.Service` (SYSTEM) | respawn the overlay if killed; **DisableTaskMgr** for the locked session while locked, restored on unlock, with a startup failsafe |

**Why a black cover under the WinUI window:** WinUI launch takes ~1–2 s and the
app is killable. The overlay's black Win32 cover appears instantly and *stays* —
if the WinUI lock is killed or slow, the desktop is still covered and input still
blocked, and the overlay relaunches the WinUI layer. The pretty UI is best-effort
on top of a hard floor.

## Multi-monitor

- **Primary display:** the full lock card (title, shutdown→logoff countdown,
  message, +15 / +30 / +1 h, passcode field, Unlock, Log Off).
- **Every other display:** a fullscreen WinUI cover — app background, a large
  centred lock glyph, and a **"Move lock here"** button. Pressing it relocates the
  primary card onto that monitor. This is a **failover**: if Windows reports the
  wrong primary, the parent can still reach the card.

## Overlay ↔ lock-app protocol (settings keys, mirrors `tray_command`)

The WinUI app verifies the passcode in-process (`PasscodeHash.Verify`) and writes
only an **action**, never the passcode (no secret ever hits disk):

- `lock_action` ∈ `extend15 | extend30 | extend60 | unlock | ignore_schedule | logoff`
- `lock_action_at` — unix seconds; the overlay ignores actions older than 60 s.

Overlay tick consumes `lock_action`, applies it to its in-memory state
(`TimeKeeper.Extend`, `ScheduleOverride`, `IgnoreScheduleUntilRestart`, or
`LockNative.Logoff`), clears the key, and re-evaluates `ShouldBlock`. When no
longer blocked it drops the black cover. The block reason (budget vs schedule)
is recomputed by the app from the same persisted settings the overlay uses.

## Keystroke blocking

Stays in the **overlay** (always alive while locked), not the WinUI app, so a
killed UI never opens a keyboard gap. The hook swallows the bypass combos but lets
normal keys through to the focused passcode field.

## SYSTEM hardening (DisableTaskMgr) — failsafe

- While a session is locked the service sets, in that user's hive,
  `HKU\<sid>\Software\Microsoft\Windows\CurrentVersion\Policies\System\DisableTaskMgr = 1`.
- On unlock it deletes the value.
- **Failsafe:** on every service start it clears any `DisableTaskMgr` it may have
  left set, so a crash-while-locked can never leave Task Manager permanently
  disabled. The service also only ever touches this one value it owns.

## Residuals (documented, not closed here)

- Ctrl+Alt+Del's Secure Attention Sequence can't be intercepted without a
  credential provider (out of scope). With Task Manager policy-disabled, the
  CAD menu's useful escape (Task Manager) is gone; Switch User / Sign out just
  trigger the enforced logoff.
- Unlock-flag forging needs DB write access — closed only by C1.

## Build increments

1. WinUI `LockWindow` + per-monitor `LockCoverWindow` + `LockController` (LL hook,
   fullscreen, display enumeration, action protocol) + `--lock` routing.
2. Overlay: replace the GDI card with a black cover that launches/relaunches the
   WinUI lock and applies `lock_action`.
3. Service: DisableTaskMgr hardening + failsafe.

Each increment compiles independently; the WinUI pieces are CI-verified only
(no local WinUI build) and the whole flow needs on-device validation.
