# Troubleshooting

- **"WinGet is not installed"**: install *App Installer* from the Microsoft Store, then rescan.
- **An update shows "Approval required"/"Manual update"**: it needs administrator rights, interaction, or has no approved automation. UpdateForge will not elevate by itself.
- **"Installer reported success but version did not change"**: the installer did not actually update; retry, or update manually.
- **"Application is running"**: close the application and retry; UpdateForge never force-closes apps.
- **Scheduled task not running**: Settings shows its next run; tasks run only while you are signed in, and not on battery/metered unless allowed.
- **Reporting a bug**: `updateforge-cli diagnostics export` creates a redacted ZIP; review before attaching.
- Logs: `%LOCALAPPDATA%\UpdateForge\logs`.
