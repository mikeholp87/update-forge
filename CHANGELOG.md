# Changelog

## [Unreleased]
### Added
- Recommendations (Linux app: *Recommended*; CLI: `updateforge-cli recommend`): packages that fit what is already installed, across apt, Flatpak, Homebrew, Cargo, pipx and npm, each with the reason and a usage tip. A reviewed local catalog; nothing is fetched. Also flags programs installed through two managers, held packages and managers that are turned off. Suggestions install through the same confirmed path as Discover and can be dismissed.
- Recommendations learn your interests. Programs are sorted into areas (gaming, video and audio, development, the command line, servers, security, backups, writing, containers, ...); new programs, accepted suggestions and dismissed ones adjust a local profile that fades with a 90 day half-life, and clear interests bring more suggestions in that area with the reason. The page shows what was learned, with a switch to turn learning off and a button to erase it. Nothing is sent anywhere, and the profile is kept out of diagnostics exports.

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
