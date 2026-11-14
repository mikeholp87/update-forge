# Changelog

## [Unreleased]

## [0.3.0] - 2026-11-14
### Added
- Linux providers for pnpm, Bun, Yarn Classic (1.x), Cargo, pipx and Homebrew.
### Changed
- Redesigned Linux GUI: graphite theme, IBM Plex fonts, dashboard that answers first.

## [0.2.0] - 2026-11-14
### Added
- Linux support: apt, Flatpak, Snap and npm providers; Avalonia GUI (`updateforge`); Linux CLI; systemd user timer scheduling; NetworkManager metered-connection check; file-lock engine lock; Debian version ordering; `.deb` and tarball packaging (`scripts/publish-linux.sh`) and a Linux CI job.
### Changed
- `UpdateForge.Background` is now cross-platform; Task Scheduler and the WinRT cost probe moved to `UpdateForge.Background.Windows`. The CLI multi-targets `net10.0` and `net10.0-windows` (`scripts/publish.ps1` selects the Windows target).
- npm provider works on Linux/macOS layouts and uses `Environment.IsPrivilegedProcess` for the elevation hint.
- Default enabled providers depend on the OS.

## [0.1.0] - 2026-11-14
### Added
- Inventory from WinGet, Windows registry, MSIX/Store, Scoop and Chocolatey with strict identity merge.
- Update engine with state machine, policy (exclusions, pins, skipped versions), verification and crash recovery.
- WPF Fluent UI, tray, Task Scheduler integration, CLI, redacted diagnostics export.
- Per-user MSI and portable ZIP packaging, CI workflows.
- Discover tab: search WinGet, Scoop, Chocolatey and npm, install from results, double-click for package details.
### Known gaps
See README "Known limitations".
