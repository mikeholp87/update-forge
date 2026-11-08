# Architecture

Clean-architecture layering; dependencies point inward.

```
Desktop (WPF, MVVM) ─┐
Cli ─────────────────┼─> Application ──> Core
Background ──────────┘        ▲
Infrastructure (SQLite, Serilog, mutex lock) ┘
Providers.* (WinGet, Windows[registry+MSIX], Scoop, Chocolatey) ──> Core
```

- **Core**: models (`InstalledApplication`, status/support enums), `IUpdateProvider`, version comparison (`AppVersion`, unknown-safe), `InventoryMerger` (identity/dedupe), `JobStateMachine`, `UpdatePolicy`, `ProcessRunner`.
- **Application**: `InventoryService` (parallel provider scan, failures isolated), `UpdateEngine` (sequential, policy → state machine → provider → verification → history), `AppSettings` (validated), store interfaces, `IEngineLock`.
- **Infrastructure**: `Database` with versioned transactional migrations (`PRAGMA user_version`, refuses newer schemas), SQLite stores, `MutexEngineLock` (one engine across GUI/CLI/task), Serilog, `DiagnosticsExporter`.
- **Background**: `ScheduledTaskService` (Task Scheduler, user context) and `BackgroundRunner` (battery/metered checks, unattended policy).
- **Desktop/CLI**: thin front ends sharing the same services. No business logic in code-behind.

Adding a provider = implement `IUpdateProvider`, register it in DI. The registry, scheduler and UI need no changes.

State machine: Discovered → Checking → UpdateAvailable → Approved → Queued → Downloading → Verifying → Installing → VerifyingInstallation → Completed, plus Failed, RestartRequired, Skipped, Blocked, Canceled, ManualActionRequired. Cancellation: *soft* (stop starting new items) is the default; *hard* (kill the running installer) exists in the engine API but is not exposed in the UI because interrupting an installer can leave a partial install.

Future (not implemented): developer-ecosystem providers (npm, pip, Cargo, .NET tools) disabled by default; community manifests; provider SDK.
