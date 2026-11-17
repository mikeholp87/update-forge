using UpdateForge.Core.Models;

namespace UpdateForge.Core.Recommendations;

/// <summary>Every lower-case name an installed program answers to: its display name, package id, and a Flatpak id's last segment.</summary>
internal static class AppKeys
{
    public static IReadOnlyList<string> Of(InstalledApplication a)
    {
        var keys = new List<string> { a.Name.Trim().ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(a.PackageId))
        {
            keys.Add(a.PackageId.Trim().ToLowerInvariant());
            if (a.ProviderId.Equals("flatpak", StringComparison.OrdinalIgnoreCase) && a.PackageId.Contains('.'))
                keys.Add(a.PackageId[(a.PackageId.LastIndexOf('.') + 1)..].ToLowerInvariant());
            // WinGet ids are Publisher.Product[.Variant]: "VideoLAN.VLC" answers to "vlc" and "Git.Git" to "git".
            if (a.ProviderId.Equals("winget", StringComparison.OrdinalIgnoreCase) && a.PackageId.Contains('.'))
                foreach (var part in a.PackageId.Split('.'))
                    if (part.Length >= 3) keys.Add(part.ToLowerInvariant());
        }
        return keys;
    }
}
