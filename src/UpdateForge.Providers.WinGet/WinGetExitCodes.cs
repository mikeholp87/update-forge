using UpdateForge.Core.Models;

namespace UpdateForge.Providers.WinGet;

/// <summary>
/// Maps WinGet process exit codes to UpdateForge outcomes. Codes come from Microsoft's published table
/// (winget-cli doc/windows/package-manager/winget/returnCodes.md).
/// </summary>
public static class WinGetExitCodes
{
    public static UpdateResult Map(int exitCode, string? details)
    {
        var code = unchecked((uint)exitCode);
        UpdateResult R(UpdateOutcome o, ErrorCategory e, string msg, bool restart = false) =>
            new() { Outcome = o, Error = e, Message = msg, ExitCode = exitCode, TechnicalDetails = details, RestartRequired = restart };

        return code switch
        {
            0 => R(UpdateOutcome.Succeeded, ErrorCategory.None, "Update installed."),
            0x8A15002B or 0x8A15004F => R(UpdateOutcome.NoChange, ErrorCategory.None, "WinGet found no applicable newer version."),
            0x8A150109 or 0x8A15010B => R(UpdateOutcome.RestartRequired, ErrorCategory.PendingRestart, "Update installed; a restart is required to finish.", true),
            0x8A15010A => R(UpdateOutcome.Failed, ErrorCategory.PendingRestart, "A restart is required before this update can be installed.", true),
            0x8A150011 or 0x8A15002D or 0x8A15005E => R(UpdateOutcome.Blocked, ErrorCategory.HashMismatch, "Installer failed integrity or security verification. The update was not installed."),
            0x8A15010F or 0x8A15003A or 0x8A15001B or 0x8A15001C => R(UpdateOutcome.Blocked, ErrorCategory.SecurityBlocked, "Blocked by organization policy."),
            0x8A150041 or 0x8A150046 => R(UpdateOutcome.ManualActionRequired, ErrorCategory.AgreementsNotAccepted, "Package or source agreements must be reviewed and accepted first."),
            0x8A150019 or 0x8A15007D => R(UpdateOutcome.ManualActionRequired, ErrorCategory.ElevationRequired, "Administrator approval is required for this update."),
            0x8A150056 => R(UpdateOutcome.ManualActionRequired, ErrorCategory.ElevationRequired, "This installer cannot run from an elevated context."),
            0x8A150101 or 0x8A150103 or 0x8A150111 => R(UpdateOutcome.Failed, ErrorCategory.ApplicationInUse, "The application is running. Close it and retry."),
            0x8A150102 => R(UpdateOutcome.Failed, ErrorCategory.InstallerRunning, "Another installation is in progress. Try again later."),
            0x8A150105 => R(UpdateOutcome.Failed, ErrorCategory.DiskSpace, "Not enough disk space."),
            0x8A150107 or 0x8A150008 or 0x8A15002E or 0x8A150086 => R(UpdateOutcome.Failed, ErrorCategory.Network, "Download failed. Check the network connection."),
            0x8A150068 => R(UpdateOutcome.Blocked, ErrorCategory.None, "The package is pinned and will not be upgraded."),
            0x8A15010C or 0x8A150005 or 0x8A15006A => R(UpdateOutcome.Canceled, ErrorCategory.Canceled, "The installation was canceled."),
            0x8A150010 or 0x8A150113 => R(UpdateOutcome.Failed, ErrorCategory.NoApplicableInstaller, "No installer applies to this system."),
            0x8A15008E => R(UpdateOutcome.ManualActionRequired, ErrorCategory.UserInteractionRequired,
                "WinGet can't upgrade this in place: the installed copy was set up a different way than the new version (a different install type). Uninstall it, then install the latest version."),
            0x8A150050 or 0x8A150114 => R(UpdateOutcome.ManualActionRequired, ErrorCategory.UserInteractionRequired, "WinGet cannot upgrade this package automatically."),
            0x8A150014 or 0x8A150016 or 0x8A150017 => R(UpdateOutcome.Failed, ErrorCategory.ProviderUnavailable, "WinGet could not resolve exactly one package."),
            0x8A15003D or 0x8A15006D or 0x8A150045 or 0x8A15004B => R(UpdateOutcome.Failed, ErrorCategory.ProviderUnavailable, "The package source is unavailable."),
            _ => R(UpdateOutcome.Failed, ErrorCategory.InstallerFailed, $"The installer reported error 0x{code:X8}."),
        };
    }
}
