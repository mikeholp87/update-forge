# Security policy and trust model

## Reporting a vulnerability

Please use GitHub **private security advisories** for this repository (Security tab, "Report a vulnerability"). Do not open public issues for vulnerabilities.

## Trust model

An updater downloads and runs installers, so UpdateForge delegates that to the package managers' own verified pipelines and keeps its own surface small.

| Boundary | Rule |
|---|---|
| Execution | Fixed executable paths (WinGet in the user's WindowsApps, Scoop/Chocolatey in their install roots); arguments passed as a typed list, never a shell string. |
| Input validation | Package IDs, sources and versions are matched against strict allowlist regexes; anything else is refused, not sanitized. Covered by injection tests. |
| Integrity | Package-manager hash and security checks are never disabled: no `--force`, `--ignore-security-hash`, `--ignore-checksums`. Hash mismatch maps to a *Blocked* result. |
| Pins/holds | WinGet pins, Scoop holds and Chocolatey pins are honored; UpdateForge exclusions and skipped versions apply to GUI, CLI and background alike (single `UpdatePolicy`). |
| Privilege | UpdateForge runs as the invoking user (`asInvoker`). The scheduled task is user-context, limited, "run only when logged on", and never stores a password. Updates needing elevation are left pending. Chocolatey upgrades run only if UpdateForge itself was started elevated. |
| Identity | Registry rows merge into a package only on strict, unambiguous matches; ambiguous or low-confidence matches never auto-update. |
| Restarts | Never automatic. |
| Registry | Read-only; uninstall strings are never executed or stored. No `Win32_Product`/WMI. |
| Data | Local SQLite and logs under `%LOCALAPPDATA%\UpdateForge`. No credentials stored. Diagnostics export redacts user, machine, e-mails, secrets and long hex tokens. |
| Network | UpdateForge itself makes no network calls (package managers do). No telemetry. |

## Not yet implemented (be aware)

- No elevated helper: therefore no *Managed Automatic* mode.
- No self-update mechanism yet; update UpdateForge by installing a new release (verify the published SHA-256).
- Release artifacts are unsigned unless a maintainer configures Authenticode signing; private keys must never be committed.
- WinGet is driven via CLI; its source/manifest trust is WinGet's.
