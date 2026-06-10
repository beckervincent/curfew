# Curfew

Parental screen time manager for Windows. Set daily limits — when time is up, the screen locks until you enter your passcode.

![Lock screen](images/lock-screen.png)

## Features

- Daily time limits per weekday, with an always-visible countdown timer
- Configurable warnings before time runs out
- Full-screen lock on all monitors, passcode-protected, with +15/30/60 min extensions
- Pause budget for breaks (45 min/day by default) and auto-pause when idle
- Optional shutdown countdown on the lock screen
- Survives restarts; runs as a system service

## Install

1. Download the latest installer from [Releases](https://github.com/beckervincent/curfew/releases).
2. Run it as Administrator and follow the wizard.
3. The default passcode is `0000` — change it right away (tray icon → Settings…).

Requires Windows 10/11 (64-bit).

## Build

```powershell
.\build.ps1   # release build + lint
```

Every push is checked by CI. Pushing a `v*` tag (or running `./release.sh`) builds the installer and publishes a GitHub release automatically. The bundled service wrapper comes from [beckervincent/nssm-fork](https://github.com/beckervincent/nssm-fork) — its latest release is pulled in during the installer build.

## Thanks

- [Simon Pamies (@spamsch)](https://github.com/spamsch)

## License

[MIT](LICENSE)
