# Architecture

Clean-architecture layering; dependencies point inward.

```
Desktop (WPF, Windows) ─┐
Desktop.Avalonia (Linux)┤
Cli (net10.0 + -windows)┼─> Application ──> Core
Background (+ .Windows) ┘        ▲
Infrastructure (SQLite, Serilog, engine lock) ┘
Providers.* (WinGet, Windows[registry+MSIX], Scoop, Chocolatey, Linux[apt, Flatpak, Snap, pnpm, Bun, Yarn, Cargo, pipx, Homebrew], Npm) ──> Core
```

- **Core**: models (`InstalledApplication`, status/support enums), `IUpdateProvider`, version comparison (`AppVersion`, unknown-safe), `InventoryMerger` (identity/dedupe), `JobStateMachine`, `UpdatePolicy`, `ProcessRunner`.
- **Application**: `InventoryService` (parallel provider scan, failures isolated), `UpdateEngine` (sequential, policy → state machine → provider → verification → history), `AppSettings` (validated), store interfaces, `IEngineLock`.
- **Infrastructure**: `Database` with versioned transactional migrations (`PRAGMA user_version`, refuses newer schemas), SQLite stores, `MutexEngineLock` (one engine across GUI/CLI/task: a named semaphore on Windows, an exclusive lock file under `$XDG_RUNTIME_DIR` elsewhere), Serilog, `DiagnosticsExporter`.
- **Background**: `BackgroundRunner` (unattended policy, metered check through `INetworkCostProbe`), `SystemdScheduleService` (systemd user timer) and `NmcliNetworkCostProbe`. `Background.Windows` holds `ScheduledTaskService` (Task Scheduler, user context) and the WinRT cost probe.
- **Desktop/CLI**: `Desktop` is WPF (Windows); `Desktop.Avalonia` is the Linux GUI and links the WPF-free view models (`AppRow`, `History`, `PackageDetails`) from it while carrying its own Shell/Discover/Settings/PackageManagers view models. The CLI multi-targets `net10.0` (Linux providers) and `net10.0-windows` (Windows providers). Thin front ends sharing the same services. No business logic in code-behind.

`IUpdateProvider.CompareVersions` lets a manager supply its own version ordering (apt uses Debian rules); the engine uses it for post-install verification.

Adding a provider = implement `IUpdateProvider`, register it in DI. The registry, scheduler and UI need no changes.

State machine: Discovered → Checking → UpdateAvailable → Approved → Queued → Downloading → Verifying → Installing → VerifyingInstallation → Completed, plus Failed, RestartRequired, Skipped, Blocked, Canceled, ManualActionRequired. Cancellation: *soft* (stop starting new items) is the default; *hard* (kill the running installer) exists in the engine API but is not exposed in the UI because interrupting an installer can leave a partial install.

Future (not implemented): developer-ecosystem providers (npm, pip, Cargo, .NET tools) disabled by default; community manifests; provider SDK.
