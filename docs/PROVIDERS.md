# Providers

| Id | Discovers | Updates | Notes |
|---|---|---|---|
| `winget` | yes | yes | CLI backend; pins honored; msstore source = approval required |
| `registry` | yes (read-only) | no | inventory only; folded into packages only on unambiguous match |
| `msix` | yes | no | Store handoff only |
| `scoop` | yes | yes (user scope) | held = pinned; global installs need admin; fixture-tested only |
| `chocolatey` | yes | only if UpdateForge is elevated | pinned honored; fixture-tested only |

## Writing a provider
Implement `IUpdateProvider` (`CheckAvailabilityAsync`, `DiscoverAsync`, `UpdateAsync`, optional `GetInstalledVersionAsync` for verification). Rules: fixed executable path, typed args, allowlist-validate every identifier, never add force/integrity-bypass flags, return `NotInstalled` for optional tools that are absent, report pins, and set `Support` honestly. Add unit tests including injection attempts. Register in DI in the Desktop and CLI hosts.
