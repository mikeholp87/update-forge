using System.Text;
using UpdateForge.Core.Models;

namespace UpdateForge.Core.Recommendations;

public enum DiscoveryOrigin
{
    /// <summary>Found in this computer's own package metadata because something already installed recommends or suggests it.</summary>
    Related,
    /// <summary>Taken from a public popularity list for an area the user is into (opt-in, see <see cref="IDiscoverySource.RequiresNetwork"/>).</summary>
    Online,
}

/// <summary>A package found outside the reviewed catalog. Every text field is untrusted: it is cleaned before it is shown.</summary>
/// <param name="Niche">The interest area it belongs to, or null when the source cannot tell.</param>
/// <param name="RelatedTo">Installed packages that point at it (apt Recommends/Suggests); empty for online results.</param>
/// <param name="Popularity">Monthly installs for online results, otherwise 0.</param>
public sealed record DiscoveredPackage(string ProviderId, string PackageId, string Name, string Summary, string? Niche, DiscoveryOrigin Origin,
    IReadOnlyList<string> RelatedTo, string? Source = null, long Popularity = 0);

/// <param name="Niches">Interest areas the user is into right now, strongest first. Online sources ask only about these, never about installed software.</param>
public sealed record DiscoveryRequest(IReadOnlyList<InstalledApplication> Installed, IReadOnlyList<string> Niches);

/// <summary>Finds candidate packages beyond the catalog. Implementations must not send anything about the installed software anywhere.</summary>
public interface IDiscoverySource
{
    /// <summary>True when the source contacts a server. Such sources only run after the user opts in.</summary>
    bool RequiresNetwork { get; }

    Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(DiscoveryRequest request, CancellationToken ct);
}

/// <summary>One application launcher on this computer (a freedesktop <c>.desktop</c> file).</summary>
public sealed record LauncherEntry(string Name, IReadOnlyList<string> Categories);

/// <summary>What the operating system knows about this computer beyond the package list. All of it is read locally.</summary>
public sealed record SystemSignals
{
    /// <summary>Apps with a menu entry and the categories their authors gave them, which classifies programs the catalog has never heard of.</summary>
    public IReadOnlyList<LauncherEntry> Launchers { get; init; } = [];

    /// <summary>When a package was installed, keyed by lower-case package name. Lets the first look learn from history instead of only a baseline.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> InstalledAt { get; init; } = new Dictionary<string, DateTimeOffset>();
}

public interface ISystemSignalSource
{
    Task<SystemSignals> ReadAsync(CancellationToken ct);
}

/// <summary>Maps the category vocabularies of freedesktop launchers, Debian sections and Flathub onto the interest areas in <see cref="Niches"/>.</summary>
public static class CategoryNiches
{
    private static readonly Dictionary<string, string> Desktop = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game"] = "gaming", ["Emulator"] = "gaming",
        ["Video"] = "media", ["AudioVideo"] = "media", ["Player"] = "media", ["Recorder"] = "media", ["TV"] = "media", ["AudioVideoEditing"] = "media",
        ["Audio"] = "music", ["Music"] = "music", ["Midi"] = "music", ["Sequencer"] = "music", ["Mixer"] = "music",
        ["Graphics"] = "creative", ["2DGraphics"] = "creative", ["3DGraphics"] = "creative", ["RasterGraphics"] = "creative", ["VectorGraphics"] = "creative",
        ["Photography"] = "creative", ["Scanning"] = "creative",
        ["Development"] = "development", ["IDE"] = "development", ["Debugger"] = "development", ["RevisionControl"] = "development",
        ["WebDevelopment"] = "development", ["GUIDesigner"] = "development", ["Building"] = "development",
        ["TerminalEmulator"] = "terminal",
        ["RemoteAccess"] = "sysadmin", ["Monitor"] = "sysadmin",
        ["Security"] = "security",
        ["Office"] = "office", ["WordProcessor"] = "office", ["Spreadsheet"] = "office", ["Presentation"] = "office", ["Publishing"] = "office",
        ["Literature"] = "office", ["Dictionary"] = "office",
    };

    private static readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["games"] = "gaming", ["sound"] = "music", ["video"] = "media", ["graphics"] = "creative", ["devel"] = "development", ["vcs"] = "development",
        ["rust"] = "development", ["golang"] = "development", ["tex"] = "office",
    };

    private static readonly Dictionary<string, string> Flathub = new(StringComparer.Ordinal)
    {
        ["gaming"] = "Game", ["media"] = "Video", ["creative"] = "Graphics", ["music"] = "Audio", ["development"] = "Development", ["office"] = "Office",
    };

    /// <summary>The niches a launcher's <c>Categories=</c> values point to. Unknown categories are ignored.</summary>
    public static IEnumerable<string> FromDesktopCategories(IEnumerable<string> categories) =>
        categories.Select(c => Desktop.GetValueOrDefault(c.Trim())).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.Ordinal);

    /// <summary>The niche for a Debian <c>Section:</c> such as <c>games</c> or <c>contrib/graphics</c>, or null.</summary>
    public static string? FromAptSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return null;
        var s = section.Trim();
        var slash = s.LastIndexOf('/');
        return Sections.GetValueOrDefault(slash >= 0 ? s[(slash + 1)..] : s);
    }

    /// <summary>The Flathub category that lists apps for a niche, or null when Flathub has no matching one.</summary>
    public static string? FlathubCategory(string niche) => Flathub.GetValueOrDefault(niche);

    /// <summary>The niche a Flathub category belongs to (the inverse of <see cref="FlathubCategory"/>), or null.</summary>
    public static string? FromFlathubCategory(string category) => Flathub.FirstOrDefault(kv => kv.Value == category).Key;
}

/// <summary>Cleans text from outside sources before it reaches the screen or a dialog.</summary>
public static class DiscoveryText
{
    /// <summary>Drops control and formatting characters (terminal escapes, bidi overrides), collapses whitespace and caps the length.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max));
        var space = false;
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }
}
