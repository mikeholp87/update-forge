namespace UpdateForge.Core.Recommendations;

public enum RecommendationKind
{
    /// <summary>A package you can install, with the reason it fits what you already have.</summary>
    Package,
    /// <summary>Advice about the current setup (duplicates, held packages, a manager that is turned off). Nothing to install.</summary>
    Tip,
}

/// <summary>Where a suggested package comes from. The install path re-validates every field.</summary>
public sealed record RecommendedPackage(string ProviderId, string PackageId, string? Source = null);

/// <param name="Key">Stable identity, used to remember that a suggestion was dismissed.</param>
/// <param name="Why">One sentence on what the package does for the user.</param>
/// <param name="Tip">A practical hint for using it, or null.</param>
/// <param name="Because">A full sentence on what made this relevant ("Because you have steam"), or null for general suggestions.</param>
/// <param name="Niches">Interest areas this belongs to; learning uses them when it is installed or dismissed.</param>
public sealed record Recommendation(
    string Key, RecommendationKind Kind, string Title, string Category, string Why, string? Tip, string? Because,
    RecommendedPackage? Package, int Priority, IReadOnlyList<string>? Niches = null);

/// <summary>Everything the engine needs. It never touches the network or the disk.</summary>
public sealed record RecommendationContext
{
    public required IReadOnlyList<UpdateForge.Core.Models.InstalledApplication> Installed { get; init; }

    /// <summary>Managers that are enabled, available and able to install packages. A suggestion is only made through one of these.</summary>
    public required IReadOnlySet<string> InstallableProviders { get; init; }

    /// <summary>Managers that are present on the system but turned off in Package managers, by display name.</summary>
    public IReadOnlyList<string> DisabledManagers { get; init; } = [];

    public IReadOnlySet<string> Dismissed { get; init; } = new HashSet<string>();

    /// <summary>What was learned so far. Empty means "use only what is installed right now".</summary>
    public InterestProfile Profile { get; init; } = new();
}
