# Research summary and decisions

Sources checked while building: winget-cli return-code table (fetched from the official repo and used verbatim for exit-code mapping), live behavior of WinGet 1.29.380 on Windows 11 26300, NuGet package listings for current stable versions.

## WinGet
- `winget export` emits locale-independent JSON (source, PackageIdentifier, Version). `list`/`upgrade`/`pin list` output is localized tables, so headers are never parsed: rows are anchored on known package IDs from the export.
- Chosen: CLI backend now; the COM API (`Microsoft.WindowsPackageManager.ComInterop` 1.29.380) needs a CsWinRT projection and was not spiked. **Tradeoff:** the CLI is slower and text-based but stable and easy to constrain. Revisit for v1.1.
- Exit codes mapped from Microsoft's table (hash mismatch, pinned, admin required, reboot, in-use, no network…).
- `winget upgrade` omits pinned packages unless `--include-pinned`, and unknown installed versions unless `--include-unknown`; both are requested so status is accurate. Unknown-version packages are shown as *Unknown*, never *Update available*.
- msstore-source packages are reported as approval-required.

## Microsoft Store / MSIX
`Windows.Management.Deployment.PackageManager.FindPackagesForUser` lists packages without admin. There is no supported API for a third-party app to check or trigger updates of other Store apps, so entries are manual with a Store handoff.

## Scoop / Chocolatey
- Scoop: `scoop export` (JSON), `scoop status`; run via `powershell.exe -NoProfile -File scoop.ps1` without bypassing execution policy. Held packages = pinned. Not live-tested.
- Chocolatey: `--limit-output` pipe-delimited `list`/`outdated` (includes pinned flag). Upgrades need admin; only attempted when already elevated. Not live-tested.

## UniGetUI / Patch My PC
Studied public behavior only (bulk updates, exclusions, scheduling, running-app handling). No code copied; UniGetUI is a separate product with its own license. Patch My PC is proprietary and was not reverse engineered.

## Stack
.NET 10 (SDK 10.0.403), WPF + WPF-UI 4.3.0 (MIT), CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.14, Serilog 4.4, TaskScheduler 2.12.2 (MIT), xunit.v3 4.0.2 on Microsoft.Testing.Platform, WiX 5.0.2 (MS-RL; v6+ introduces maintenance-fee terms so v5 was chosen). Tray uses WinForms `NotifyIcon` (WPF-UI main assembly has no tray control); notifications are balloon tips (native toasts) rather than unpackaged toast COM registration.
