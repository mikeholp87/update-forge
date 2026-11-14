# Changelog

## [Unreleased]

## [0.3.1] - 2026-11-14
### Security
- apt elevation on the .deb goes through a dedicated helper (`/usr/libexec/updateforge/updateforge-apt-helper`) and its own polkit action, `io.github.mikeholp87.updateforge.apt`: the prompt names UpdateForge, asks every time, and authorizes only refreshing sources or installing/upgrading one validated package. The portable tarball still uses plain `pkexec apt-get`.
- Local data (inventory, history, logs, background summary, locks) is owner-only on Linux: folders `0700`, files `0600`; existing installs are tightened on start.
- Running as root, per-user tools (Cargo, pnpm, Bun, Yarn, pipx, Homebrew) are looked up only in system folders, never in `PATH` or home folders.
- Single-instance socket and lock files moved out of the shared `/tmp` into the per-user runtime folder, with a same-user peer check.
- npm, pnpm, Bun and Yarn refuse package names ending in an archive extension (they would install a local file), and package managers no longer run in the folder the app was started from.
- Desktop notifications escape markup in package names from remote catalogs.
- Package files ship as 755/644 regardless of the build machine's umask.
### Added
- Recommendations (Linux app: *Recommended*; CLI: `updateforge-cli recommend`): packages that fit what is already installed, across apt, Flatpak, Homebrew, Cargo, pipx and npm, each with the reason and a usage tip. A reviewed local catalog; nothing is fetched. Also flags programs installed through two managers, held packages and managers that are turned off. Suggestions install through the same confirmed path as Discover and can be dismissed.
- Recommendations learn your interests. Programs are sorted into areas (gaming, video and audio, development, the command line, servers, security, backups, writing, containers, ...); new programs, accepted suggestions and dismissed ones adjust a local profile that fades with a 90 day half-life, and clear interests bring more suggestions in that area with the reason. The page shows what was learned, with a switch to turn learning off and a button to erase it. Nothing is sent anywhere, and the profile is kept out of diagnostics exports.
### Fixed
- pnpm (and any tool whose launcher has no `#!` line) failed with "Exec format error"; such launchers now run through `/bin/sh`.

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
