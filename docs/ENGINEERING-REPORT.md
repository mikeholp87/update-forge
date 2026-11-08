# Engineering report: UpdateForge 0.1.0 release candidate

## Implemented
Core domain and update engine; WinGet (CLI), registry, MSIX, Scoop, Chocolatey providers; SQLite persistence with migrations; WPF Fluent UI (dashboard, applications, queue, history, settings), tray, balloon notifications; Task Scheduler integration and background runner; CLI; redacted diagnostics export; publish script, portable ZIP, per-user WiX MSI; CI/release workflows (SHA-pinned actions), Dependabot, issue templates, docs.

## Verified (actually run)
- Solution builds in Release with warnings-as-errors; **127 unit/engine/provider tests pass** (version compare, merge/dedupe, policy, state machine, WinGet parsing/exit codes/injection refusal, Scoop/Chocolatey fixtures, engine with fake provider incl. verification failure, crash recovery, busy lock, cancellation, DB migrations, redaction).
- **Real read-only scans on this Windows 11 machine**: WinGet results matched raw `winget upgrade`; 136 merged apps; scan wall time 4.4-4.9 s (includes network-bound WinGet); idle GUI CPU 0%, ~240-260 MB working set; published self-contained GUI/CLI launch and run; diagnostics ZIP verified to contain no username/profile/machine name.
- Real-data scans exposed and fixed two defects (HKCU double-counting; fold downgrading package confidence).
- MSI and portable ZIP (77 MB; MSI 65 MB) build.

## NOT verified / not done (do not assume)
- **No real update has been installed** by UpdateForge (deliberately; this is the developer machine). Update All, hash-mismatch, reboot and elevation paths are tested only with fakes and exit-code mapping.
- MSI install/upgrade/uninstall, scheduled-task creation/firing, notifications, tray menu actions, battery/metered behavior: not exercised.
- Scoop and Chocolatey: not installed here; fixture tests only.
- Not implemented: WinGet COM backend, portable-app provider, self-update, elevated helper / Managed mode, localization, ARM64, UI automation tests, theme/DPI tests, Authenticode signing, export inventory, per-provider pin editing UI, running-app detection, restart scheduling workflow, skipped-version/blocked-source management UI, hard-cancel in UI.
- Screenshot used a PowerShell capture with DPI mismatch; layout at other scalings unchecked.

## Packaging
`scripts/publish.ps1` (portable ZIP + SHA-256); `src/UpdateForge.Installer` (WiX 5, per-user, ICE38/60/64/91 suppressed by design); uninstall removes the scheduled task and keeps `%LOCALAPPDATA%\UpdateForge` (no purge UI yet).

## Before a public release
1. Test the MSI and Update All in a VM/Windows Sandbox with controlled test apps.
2. Install Scoop/Chocolatey in a VM and verify those providers.
3. Add UI automation, self-update (signed/hash-verified), signing, localization.
4. Create the GitHub repo (needs your authorization), enable private advisories and `release` environment reviewers.
