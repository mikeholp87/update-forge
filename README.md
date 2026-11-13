# UpdateForge

Website: <https://mikeholp87.github.io/update-forge/>

A free, open-source (MIT) software updater for Windows 11 and compatible Windows 10. It inventories what is installed, shows which applications have a newer version available, and installs updates through trusted package managers, on demand or on a schedule. No telemetry, no accounts, no ads.

> **Status: 0.1.0 release candidate, not yet released.** See [Known limitations](#known-limitations) and [docs/ENGINEERING-REPORT.md](docs/ENGINEERING-REPORT.md) for exactly what has and has not been tested.

## Features

- **Discovery**: WinGet (installed packages, available versions, pins), Windows uninstall registry (read-only), Microsoft Store/MSIX packages, Scoop and Chocolatey when installed.
- **Honest status**: every application is *Update ready*, *Approval required*, *Manual update*, *No update method* or *Unknown*. UpdateForge never reports an unknown version as "newer" and never invents installers or switches.
- **Update engine**: sequential queue, per-item state machine, post-install verification, restart detection, crash recovery (an interrupted install is never reported as success).
- **Fluent WPF UI**: dashboard, installed applications, queue, history, settings; light/dark; tray icon.
- **Scheduling**: a per-user Windows Task Scheduler task runs scans (and, if enabled, safe updates) even when the window is closed.
- **CLI**: `updateforge-cli scan | list | outdated | update --all | update --id <id> | history | providers | doctor | schedule | diagnostics export` (`--json` supported).
- **Diagnostics**: redacted ZIP for bug reports.

## Requirements

Windows 11 x64 (Windows 10 2004+ expected to work, untested). WinGet (App Installer) is needed for update installation.

## Install

Release artifacts (when published): per-user MSI (no admin needed) and a portable ZIP, each with SHA-256 sums. Unsigned builds may show a SmartScreen warning; UpdateForge does not suppress it.

## How automatic updates work

Modes: **Manual** (default, check only) and **Automatic Safe** (scheduled runs install only supported, non-interactive, non-elevated updates; anything else stays pending and you are notified). *Managed Automatic* is intentionally disabled until an elevated execution mechanism has been independently tested. The scheduled task never runs as SYSTEM and never elevates.

## Security model

See [SECURITY.md](SECURITY.md). In short: official package-manager CLIs at fixed paths, strictly validated typed arguments (no shell strings), no `--force`/hash-bypass flags, pins and holds honored, no automatic restarts.

## Known limitations

- Windows cannot update arbitrary third-party software; registry-only apps are inventory-only.
- Store apps: no supported API exists to check or trigger updates for other apps; UpdateForge offers an *Open Microsoft Store* handoff.
- Packages needing administrator rights are never installed unattended; Chocolatey upgrades require running UpdateForge elevated.
- WinGet integration uses the CLI (anchored on locale-independent `winget export`), not yet the COM API.
- Scoop/Chocolatey providers are fixture-tested only; Portable-app provider, self-update, localization, ARM64 and UI automation tests are not implemented yet.

## Build from source

```powershell
dotnet build UpdateForge.CI.slnf -c Release   # everything except the MSI installer, which needs the publish step below
dotnet test --project tests/UpdateForge.Core.Tests
./scripts/publish.ps1 -Version 0.1.0     # portable ZIP
dotnet build src/UpdateForge.Installer/UpdateForge.Installer.wixproj -c Release -p:Version=0.1.0
```

Requires the .NET 10 SDK. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT, see [LICENSE](LICENSE).

## Acknowledgments

[WPF UI](https://github.com/lepoco/wpfui) (MIT), [TaskScheduler](https://github.com/dahall/TaskScheduler) (MIT), Serilog (Apache-2.0), Microsoft.Data.Sqlite (MIT), CommunityToolkit.Mvvm (MIT), WiX Toolset (MS-RL, build-time only). Design informed by public documentation of WinGet, Scoop, Chocolatey, UniGetUI and Patch My PC; no code was copied from them.
