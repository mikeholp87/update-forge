# UpdateForge

Website: <https://mikeholp87.github.io/update-forge/>

A free, open-source (MIT) software updater for **Windows 11** (and compatible Windows 10) and **Linux**. It inventories what is installed, shows which applications have a newer version available, and installs updates through trusted package managers, on demand or on a schedule. No telemetry, no accounts, no ads.

> **Status: 0.2.0.** See [Known limitations](#known-limitations) and [docs/ENGINEERING-REPORT.md](docs/ENGINEERING-REPORT.md) for exactly what has and has not been tested.

## Features

- **Discovery**: WinGet (installed packages, available versions, pins), Windows uninstall registry (read-only), Microsoft Store/MSIX packages, Scoop, Chocolatey and npm (global packages) when installed.
- **Discover packages**: search enabled managers and their configured catalogs in parallel, with results appearing as each manager finishes. Recent searches are reused for up to two minutes; Refresh fetches fresh results. Cancellation keeps packages already found. Every result has an **Install** button for WinGet, Scoop, Chocolatey and npm: it asks for confirmation first, skips packages that are already installed, takes the same lock as update runs so the two never overlap, and records the attempt in History.
- **npm**: global packages are inventoried, checked against the registry, and updated by installing the exact version that was checked. npm is run as `node.exe npm-cli.js` directly (never `npm.cmd` or a shell), package names and versions are validated, and packages installed with `npm link` are treated as held. Permission errors are reported as needing administrator rights.
- **Honest status**: every application is *Update ready*, *Approval required*, *Manual update*, *No update method* or *Unknown*. UpdateForge never reports an unknown version as "newer" and never invents installers or switches.
- **Update engine**: sequential queue, per-item state machine, post-install verification, restart detection, crash recovery (an interrupted install is never reported as success).
- **Fluent WPF UI**: dashboard, installed applications, queue, history, settings; light/dark; tray icon.
- **Package managers**: a dedicated page for provider health, installed CLI versions and paths, enabled providers, configured sources, and WinGet/Scoop source refresh. Setup links open each manager's official instructions. Source additions/removals remain CLI operations.
- **Scheduling**: a per-user Windows Task Scheduler task runs scans (and, if enabled, safe updates) even when the window is closed.
- **Export and bundles**: export the installed list (or just the updates) to CSV or JSON, from the app or with `updateforge-cli export`. `updateforge-cli bundle export <file>` saves your WinGet packages to a bundle; `bundle import <file>` shows what is missing and `--install` installs it. Bundles are untrusted input: ids, sources and versions are validated and nothing outside WinGet/msstore is ever run.
- **CLI**: `updateforge-cli scan | list | outdated | update --all | update --id <id> | export | bundle export|import | history | providers | doctor | schedule | diagnostics export` (`--json` supported).
- **Diagnostics**: redacted ZIP for bug reports.

## Requirements

**Linux**: x64 or arm64 with a desktop session (X11 or Wayland) for the GUI; the CLI runs anywhere. Providers: apt, Flatpak, Snap, npm, pnpm, Bun, Yarn Classic, Cargo, pipx and Homebrew, each used when installed. See [Linux](#linux).

**Windows**: Windows 11 x64 (Windows 10 2004+ expected to work, untested). WinGet (App Installer) is needed for update installation.

## Linux

The Linux build shares the engine, database, CLI and policy with the Windows build. What differs:

- **Providers**: `apt` (packages you installed yourself plus anything upgradable), `flatpak` (apps, per user and system installation), `snap`, `npm`, and the per-user tools `pnpm`, `bun`, `yarn` (Classic 1.x only), `cargo` (crates from `cargo install`), `pipx` and `brew` (Homebrew formulae and casks). The per-user tools are looked up on PATH and in their usual install folders, never run as root, and validate every package name before it reaches a command line. Cargo, Bun, Yarn and pipx have no bulk outdated command, so each package is looked up individually. Cargo updates recompile the crate; pipx and Homebrew always move to the newest release (they cannot pin a version). Debian version ordering (epochs, `~`) is implemented, so `1:2.0` correctly beats `1.9`.
- **Privileges**: apt upgrades need root, so UpdateForge runs `apt-get` through `pkexec` (polkit shows the password prompt) and never stores or asks for your password itself. Flatpak and Snap authorize through their own polkit prompts. Scheduled runs never prompt: updates needing authorization stay pending and you are notified.
- **Scheduling**: a systemd *user* timer (`~/.config/systemd/user/updateforge.timer`), no root. It fires while you are logged in; `loginctl enable-linger $USER` keeps it running after logout. Battery use maps to `ConditionACPower`, metered connections are read from NetworkManager (`nmcli`).
- **GUI**: Avalonia (`updateforge`), with a tray icon (GNOME needs the AppIndicator extension) and notifications through `notify-send`.
- **CLI**: `updateforge-cli` with the same commands (`scan`, `outdated`, `update --all`, `schedule apply`, ...). `bundle` is WinGet-only and not available.
- Data lives in `~/.local/share/UpdateForge` (database, logs).

Build and package (needs the .NET 10 SDK):

```bash
dotnet build UpdateForge.CI.slnf -c Release
dotnet test --project tests/UpdateForge.Core.Tests
./scripts/publish-linux.sh 0.2.0      # artifacts/*.deb and a portable .tar.gz
sudo apt install ./artifacts/updateforge_0.2.0_amd64.deb
```

## Install

**Debian / Ubuntu (apt)**: signed repository, updates arrive through `apt upgrade`.

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://mikeholp87.github.io/apt/apt.asc | sudo tee /etc/apt/keyrings/mikeholp87-apt.asc >/dev/null
echo "deb [signed-by=/etc/apt/keyrings/mikeholp87-apt.asc] https://mikeholp87.github.io/apt/ ./" | sudo tee /etc/apt/sources.list.d/mikeholp87-apt.list
sudo apt update && sudo apt install updateforge
```

**Windows**: release artifacts: per-user MSI (no admin needed) and a portable ZIP, each with SHA-256 sums. Unsigned builds may show a SmartScreen warning; UpdateForge does not suppress it.

## How automatic updates work

Modes: **Manual** (default, check only) and **Automatic Safe** (scheduled runs install only supported, non-interactive, non-elevated updates; anything else stays pending and you are notified). *Managed Automatic* is intentionally disabled until an elevated execution mechanism has been independently tested. The scheduled task never runs as SYSTEM and never elevates.

## Security model

See [SECURITY.md](SECURITY.md). In short: official package-manager CLIs at fixed paths, strictly validated typed arguments (no shell strings), no `--force`/hash-bypass flags, pins and holds honored, no automatic restarts.

## Known limitations

- Windows cannot update arbitrary third-party software; registry-only apps are inventory-only.
- Store apps: no supported API exists to check or trigger updates for other apps; UpdateForge offers an *Open Microsoft Store* handoff.
- Packages needing administrator rights are never installed unattended; Chocolatey installs and upgrades require running UpdateForge elevated. Installing from Discover always asks first, and npm packages can run their own install scripts, so check what you install.
- WinGet integration uses the CLI (anchored on locale-independent `winget export`), not yet the COM API.
- The pnpm, Bun, Yarn Classic, Cargo, pipx and Homebrew providers are on by default on Linux and each is skipped when its tool is not installed (turn them off under Package Managers). They are fixture-tested; Cargo, Bun, Yarn and Homebrew were also run read-only against the real tools, while pnpm and pipx were not (no working install available), so their JSON parsing follows the documented output only. No real update or install was run for any of them.
- Scoop/Chocolatey providers are fixture-tested only, and the npm provider is fixture-tested plus checked read-only against a real npm (listing, search, registry, version check; no real update or install was run). npm must be installed with the standard Node.js layout (nvm and Volta layouts are not supported yet), and Scoop cannot install a specific version; Portable-app provider, self-update, localization, ARM64 and UI automation tests are not implemented yet.

## Build from source (Windows)

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
