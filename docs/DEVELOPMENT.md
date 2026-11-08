# Development

- .NET 10 SDK; Windows 11. `nuget.config` pins nuget.org as the only package source.
- Build: `dotnet build UpdateForge.sln` (warnings are errors). Test: `dotnet test --project tests/UpdateForge.Core.Tests` (Microsoft.Testing.Platform runner set in `global.json`).
- Run GUI: `dotnet run --project src/UpdateForge.Desktop`. CLI: `dotnet run --project src/UpdateForge.Cli -- outdated`.
- Real WinGet scans are read-only. **Never run `update` commands or install the MSI on your primary machine for testing; use a VM/Windows Sandbox.**
- Data: `%LOCALAPPDATA%\UpdateForge` (`updateforge.db`, `logs\`).
- Packaging: `scripts/publish.ps1`, then the WiX project. Signing: configure a certificate as a CI secret; never commit keys.
- Manual test checklist (not automatable here): elevation prompts, real installers, reboot-required flows, scheduled-task firing on battery/metered, MSI install/upgrade/uninstall in a clean VM.
