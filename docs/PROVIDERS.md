# Providers

| Id | Discovers | Updates | Notes |
|---|---|---|---|
| `winget` | yes | yes | CLI backend; pins honored; msstore source = approval required |
| `registry` | yes (read-only) | no | inventory only; folded into packages only on unambiguous match |
| `msix` | yes | no | Store handoff only |
| `scoop` | yes | yes (user scope) | held = pinned; global installs need admin; fixture-tested only |
| `chocolatey` | yes | only if UpdateForge is elevated | pinned honored; fixture-tested only |
| `npm` | yes (global packages) | yes | runs `node npm-cli.js` directly (never `npm.cmd`/a shell; on Linux the `npm` symlink is resolved to its script); updates install the exact checked version; `npm link`ed packages are pinned; permission errors are reported as needing administrator rights |
| `apt` (Linux) | yes (manually installed + upgradable) | yes, as root via `pkexec` | `dpkg-query`/`apt list --upgradable` with the C locale; installs `pkg:arch=version` (the exact checked version); `apt-mark hold` = pinned; reboot-required marker → restart required; unattended runs leave it pending |
| `flatpak` (Linux) | yes (apps) | yes | per installation (`--user`/`--system`); flatpak asks polkit itself, never run as root; unattended runs leave system installs pending |
| `snap` (Linux) | yes (non-base snaps) | yes | `snap list` + `snap refresh --list`; held = pinned; snapd asks polkit itself; unattended runs leave refreshes pending |

Linux providers look tools up in fixed system directories (`/usr/bin`, `/usr/local/bin`, `/bin`, `/snap/bin`, `/usr/sbin`, `/sbin`), never in the caller's `PATH`. Only npm uses `PATH`, because Node is commonly installed per user (nvm, Homebrew).

Providers that can also **search** (`IPackageSearchProvider`) feed the Discover page, and providers that implement `InstallAsync` let Discover install what you find. Both re-validate every identifier from search output before it reaches a command line.

## Writing a provider
Implement `IUpdateProvider` (`CheckAvailabilityAsync`, `DiscoverAsync`, `UpdateAsync`, optional `GetInstalledVersionAsync` for verification). Rules: fixed executable path, typed args, allowlist-validate every identifier, never add force/integrity-bypass flags, return `NotInstalled` for optional tools that are absent, report pins, and set `Support` honestly. Add unit tests including injection attempts. Register in DI in the Desktop and CLI hosts.
