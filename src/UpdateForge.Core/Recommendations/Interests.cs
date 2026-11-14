using UpdateForge.Core.Models;

namespace UpdateForge.Core.Recommendations;

/// <summary>What was learned about the user, kept locally. Weights are learned signal only; interest from what is installed right now is recomputed each time.</summary>
public sealed record InterestProfile
{
    /// <summary>Niche key to learned weight. Positive: the user keeps installing in this niche. Negative: they turned suggestions down.</summary>
    public IReadOnlyDictionary<string, double> Weights { get; init; } = new Dictionary<string, double>();

    /// <summary>Program names present at the last look, so a program that is new since then can be noticed. Empty until the first look.</summary>
    public IReadOnlyList<string> SeenApps { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <param name="Niche">Stable key, e.g. "gaming".</param>
/// <param name="Score">Installed apps in the niche plus what was learned. Higher means a stronger interest.</param>
/// <param name="Apps">Up to three installed programs that show the interest, for explaining it.</param>
/// <param name="Learned">True when learning, not just the current inventory, contributes meaningfully.</param>
public sealed record Interest(string Niche, string Name, double Score, IReadOnlyList<string> Apps, bool Learned)
{
    public string Strength => Score >= 6 ? "strong" : Score >= 4 ? "clear" : "emerging";
}

/// <summary>The interest areas the catalog knows about.</summary>
public static class Niches
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["gaming"] = "gaming",
        ["media"] = "video and audio",
        ["creative"] = "graphics and photos",
        ["music"] = "music making",
        ["development"] = "development",
        ["terminal"] = "the command line",
        ["sysadmin"] = "servers and networking",
        ["security"] = "privacy and security",
        ["backup"] = "backups",
        ["office"] = "writing and research",
        ["containers"] = "containers and virtual machines",
    };

    public static IReadOnlyCollection<string> Keys => Names.Keys;

    /// <summary>Lower-case phrase for use inside a sentence.</summary>
    public static string Phrase(string key) => Names.GetValueOrDefault(key, key);

    /// <summary>Same phrase with a capital, for a heading or chip.</summary>
    public static string Title(string key) => Phrase(key) is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] : key;
}

/// <summary>
/// The learning itself: simple, local and explainable. No model and no network, just counting. Interest in a niche is
/// (programs installed in it, capped) plus a learned weight that grows when new programs appear or a suggestion is installed,
/// shrinks when a suggestion is dismissed, and fades with a 90 day half-life so old habits stop dominating.
/// </summary>
public static class InterestModel
{
    public const double Threshold = 2.0;

    private const double HalfLifeDays = 90;
    private const double NewProgramBoost = 2.0;
    private const double PerLearnCap = 6.0;
    private const double InstalledSuggestionBoost = 3.0;
    private const double DismissedSuggestionPenalty = -2.0;
    private const double MinWeight = -5, MaxWeight = 20;
    private const int MaxSeen = 3000;
    private const double StaticCap = 6;

    public static IReadOnlyList<Interest> Compute(InterestProfile profile, IReadOnlyList<InstalledApplication> installed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installed);
        var named = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var viaProvider = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var app in installed)
        {
            foreach (var niche in RecommendationCatalog.NichesByName(AppKeys.Of(app)))
            {
                if (!named.TryGetValue(niche, out var list)) named[niche] = list = [];
                if (!list.Contains(app.Name, StringComparer.OrdinalIgnoreCase)) list.Add(app.Name);
            }
            if (RecommendationCatalog.ProviderNiche.TryGetValue(app.ProviderId, out var pn)) viaProvider[pn] = viaProvider.GetValueOrDefault(pn) + 1;
        }

        var result = new List<Interest>();
        foreach (var niche in Niches.Keys)
        {
            var apps = named.GetValueOrDefault(niche) ?? [];
            // Programs from developer-oriented managers (npm, cargo, pipx...) say something even when the catalog has no name for them.
            var fromProvider = Math.Min(viaProvider.GetValueOrDefault(niche), 4) * 0.5;
            var current = Math.Min(apps.Count + fromProvider, StaticCap);
            var learned = profile.Weights.GetValueOrDefault(niche);
            var score = current + learned;
            if (score <= 0) continue;
            result.Add(new Interest(niche, Niches.Title(niche), Math.Round(score, 2), [.. apps.Take(3)], learned >= 1));
        }
        return [.. result.OrderByDescending(i => i.Score).ThenBy(i => i.Niche, StringComparer.Ordinal)];
    }

    /// <summary>Looks at the current inventory: notices programs that are new since the last look and strengthens their niches.</summary>
    public static InterestProfile Learn(InterestProfile profile, IReadOnlyList<InstalledApplication> installed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installed);
        var weights = Decayed(profile, now);
        var current = installed.Select(a => a.Name.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        // The first look only records a baseline: everything already installed is "known", not "new".
        var firstLook = profile.SeenApps.Count == 0 && profile.UpdatedAt == default;
        if (!firstLook)
        {
            var seen = profile.SeenApps.ToHashSet(StringComparer.Ordinal);
            var gained = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var app in installed.Where(a => !seen.Contains(a.Name.Trim().ToLowerInvariant())))
                foreach (var niche in RecommendationCatalog.NichesByName(AppKeys.Of(app)))
                    gained[niche] = Math.Min(PerLearnCap, gained.GetValueOrDefault(niche) + NewProgramBoost);
            foreach (var (niche, add) in gained) weights[niche] = Clamp(weights.GetValueOrDefault(niche) + add);
        }
        return new InterestProfile { Weights = weights, SeenApps = [.. current.Take(MaxSeen)], UpdatedAt = now };
    }

    /// <summary>The user installed a suggestion: that niche is a keeper.</summary>
    public static InterestProfile RecordInstalled(InterestProfile profile, IEnumerable<string> niches, DateTimeOffset now) => Adjust(profile, niches, InstalledSuggestionBoost, now);

    /// <summary>The user turned a suggestion down: ask for more evidence before suggesting that niche again.</summary>
    public static InterestProfile RecordDismissed(InterestProfile profile, IEnumerable<string> niches, DateTimeOffset now) => Adjust(profile, niches, DismissedSuggestionPenalty, now);

    private static InterestProfile Adjust(InterestProfile profile, IEnumerable<string> niches, double delta, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var weights = Decayed(profile, now);
        foreach (var niche in niches.Where(Niches.Keys.Contains)) weights[niche] = Clamp(weights.GetValueOrDefault(niche) + delta);
        return profile with { Weights = weights, UpdatedAt = now };
    }

    private static Dictionary<string, double> Decayed(InterestProfile profile, DateTimeOffset now)
    {
        var days = profile.UpdatedAt == default ? 0 : Math.Max(0, (now - profile.UpdatedAt).TotalDays);
        var factor = Math.Pow(0.5, days / HalfLifeDays);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (niche, w) in profile.Weights)
            if (Niches.Keys.Contains(niche) && Math.Abs(w * factor) >= 0.05) weights[niche] = Clamp(w * factor);
        return weights;
    }

    private static double Clamp(double w) => Math.Clamp(Math.Round(w, 3), MinWeight, MaxWeight);
}
