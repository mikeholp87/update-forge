using UpdateForge.Core.Models;

namespace UpdateForge.Application;

/// <summary>
/// Removes "updates" that the package manager has already proven it cannot perform from the update list.
/// Example: an app installed by its own setup program while WinGet's newer version is a different install type
/// (portable, MSIX, ...). WinGet lists an upgrade but always refuses it with 0x8A15008E, so offering it is noise.
/// </summary>
public static class UpgradeEligibility
{
    /// <summary>APPINSTALLER_CLI_ERROR_UPGRADE_DIFFERENT_INSTALL_TECHNOLOGY.</summary>
    public const int WinGetDifferentInstallTechnology = unchecked((int)0x8A15008E);

    public const string SeparateInstallerNote =
        "Installed with a different installer than the WinGet package, so WinGet can't upgrade it. "
        + "To let WinGet manage it, uninstall it and install it through WinGet.";

    /// <param name="history">Update history, newest first.</param>
    public static IReadOnlyList<InstalledApplication> Apply(IReadOnlyList<InstalledApplication> apps, IReadOnlyList<HistoryRecord> history)
    {
        if (history.Count == 0) return apps;
        var mismatched = history
            .Where(h => h.ExitCode == WinGetDifferentInstallTechnology && h.PreviousVersion is not null)
            .Select(h => (h.ApplicationId, Installed: h.PreviousVersion!))
            .ToHashSet();
        if (mismatched.Count == 0) return apps;

        return [.. apps.Select(a => IsSeparateInstall(a, mismatched) ? Demote(a) : a)];
    }

    // Keyed on the installed version: reinstalling through WinGet changes it, which lifts the restriction automatically.
    private static bool IsSeparateInstall(InstalledApplication a, HashSet<(string ApplicationId, string Installed)> mismatched) =>
        a.UpdateAvailable && a.InstalledVersion is not null && mismatched.Contains((a.Id, a.InstalledVersion));

    private static InstalledApplication Demote(InstalledApplication a) => a with
    {
        Status = UpdateStatus.Unknown,
        AvailableVersion = null,
        Support = UpdateSupport.Unsupported,
        Note = SeparateInstallerNote,
    };
}
