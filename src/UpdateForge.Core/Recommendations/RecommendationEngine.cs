using UpdateForge.Core.Models;

namespace UpdateForge.Core.Recommendations;

/// <summary>
/// Turns the installed inventory into suggestions. Pure and deterministic: the same inventory always yields the same list, so it is easy
/// to test and nothing about the machine leaves it.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Names that legitimately appear under two managers (the package manager's own pieces) and are not duplicate installs.</summary>
    private static readonly HashSet<string> NotDuplicates = new(StringComparer.OrdinalIgnoreCase) { "snapd", "npm", "node", "nodejs", "pnpm", "yarn", "bun" };

    private const int MaxPerNiche = 2, MaxNicheSuggestions = 8;

    public static IReadOnlyList<Recommendation> Generate(RecommendationContext context, int maxPackages = 14)
    {
        ArgumentNullException.ThrowIfNull(context);
        var have = Index(context.Installed);
        var interests = InterestModel.Compute(context.Profile, context.Installed, context.Signals).Where(i => i.Score >= InterestModel.Threshold).ToDictionary(i => i.Niche, StringComparer.Ordinal);
        var results = new List<Recommendation>();
        var nicheCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var rule in RecommendationCatalog.Rules.OrderByDescending(r => r.Priority))
        {
            if (context.Dismissed.Contains(rule.Key)) continue;
            // Already installed through any manager (by its title or any candidate name): nothing to suggest.
            if (have.Names.Contains(rule.Title.ToLowerInvariant()) || rule.Candidates.Any(c => have.Names.Contains(c.PackageId.ToLowerInvariant()))) continue;
            var niches = rule.Niches ?? [];

            string? because;
            var priority = rule.Priority;
            if (rule.When.Length > 0)
            {
                var matched = rule.When.Where(w => have.Matches(w)).ToList();
                if (matched.Count == 0) continue;
                because = rule.Broad ? null : "Because you have " + string.Join(", ", matched.Select(Display).Take(3));
            }
            else
            {
                // A niche-only suggestion needs a clear interest in one of its niches, and is limited so one niche cannot flood the list.
                var interest = niches.Select(n => interests.GetValueOrDefault(n)).Where(i => i is not null).MaxBy(i => i!.Score);
                if (interest is null || nicheCount.GetValueOrDefault(interest.Niche) >= MaxPerNiche || nicheCount.Values.Sum() >= MaxNicheSuggestions) continue;
                nicheCount[interest.Niche] = nicheCount.GetValueOrDefault(interest.Niche) + 1;
                priority = 30 + (int)Math.Min(interest.Score, 10) * 5 + rule.Priority / 10;
                because = Explain(interest);
            }

            var pick = rule.Candidates.FirstOrDefault(c => context.InstallableProviders.Contains(c.ProviderId));
            if (pick is null) continue;   // no usable manager on this machine can install it

            results.Add(new Recommendation(rule.Key, RecommendationKind.Package, rule.Title, rule.Category, rule.Why, rule.Tip, because,
                new RecommendedPackage(pick.ProviderId, pick.PackageId, pick.Source), priority, niches));
        }

        var packages = results.OrderByDescending(r => r.Priority).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Take(maxPackages).ToList();
        return [.. packages, .. FromDiscovered(context, have, interests), .. Tips(context)];
    }

    private const int MaxDiscovered = 6, MaxDiscoveredPerNiche = 2;

    /// <summary>Key under which a discovered package is remembered as dismissed.</summary>
    public static string DiscoveredKey(DiscoveredPackage p) => $"found:{p.ProviderId.ToLowerInvariant()}:{p.PackageId.ToLowerInvariant()}";

    /// <summary>
    /// Candidates from outside the catalog, after the catalog. Never something the catalog already covers (even a dismissed catalog entry),
    /// never something installed, and only through a manager that can install it here. Both kinds also need a clear interest in their niche, which
    /// keeps infrastructure that merely happens to be suggested by something installed out of the list.
    /// </summary>
    private static List<Recommendation> FromDiscovered(RecommendationContext context, Inventory have, Dictionary<string, Interest> interests)
    {
        if (context.Discovered.Count == 0) return [];
        var catalogIds = RecommendationCatalog.Rules.SelectMany(r => r.Candidates.Select(c => c.PackageId.ToLowerInvariant()).Append(r.Title.ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);

        var scored = new List<(DiscoveredPackage Package, int Score, Interest? Interest)>();
        foreach (var d in context.Discovered)
        {
            if (!context.InstallableProviders.Contains(d.ProviderId) || context.Dismissed.Contains(DiscoveredKey(d))) continue;
            var id = d.PackageId.ToLowerInvariant();
            if (catalogIds.Contains(id) || catalogIds.Contains(d.Name.ToLowerInvariant()) || have.Names.Contains(id) || have.Names.Contains(d.Name.ToLowerInvariant())) continue;
            var interest = d.Niche is null ? null : interests.GetValueOrDefault(d.Niche);
            if (interest is null) continue;
            if (d.Origin == DiscoveryOrigin.Online)
                scored.Add((d, 20 + (int)Math.Min(interest.Score, 10) * 3 + (int)(Math.Log10(Math.Max(d.Popularity, 1)) * 2), interest));
            else if (d.RelatedTo.Count > 0)
                scored.Add((d, 30 + Math.Min(d.RelatedTo.Count, 5) * 4 + (int)Math.Min(interest.Score, 10), interest));
        }

        var perNiche = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<Recommendation>();
        foreach (var (d, score, interest) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Package.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (results.Count >= MaxDiscovered) break;
            var bucket = d.Niche!;
            if (perNiche.GetValueOrDefault(bucket) >= MaxDiscoveredPerNiche) continue;
            perNiche[bucket] = perNiche.GetValueOrDefault(bucket) + 1;
            var because = d.Origin == DiscoveryOrigin.Online
                ? $"Popular on {(d.Source is null ? "the store" : char.ToUpperInvariant(d.Source[0]) + d.Source[1..])} for {Niches.Phrase(d.Niche!)}, which you're into"
                : "Suggested by " + string.Join(", ", d.RelatedTo.Take(2)) + (d.RelatedTo.Count > 2 ? $" and {d.RelatedTo.Count - 2} more" : "") + ", which you have installed";
            results.Add(new Recommendation(DiscoveredKey(d), RecommendationKind.Package, d.Name, Niches.Title(d.Niche!),
                d.Summary.Length > 0 ? d.Summary : "Found in your package sources.", null, because,
                new RecommendedPackage(d.ProviderId, d.PackageId, d.Source), score, [d.Niche!]));
        }
        return results;
    }

    /// <summary>Why a niche suggestion appears, in plain words: what was installed, or that it was learned over time.</summary>
    private static string Explain(Interest interest) =>
        interest.Apps.Count > 0
            ? $"You're into {Niches.Phrase(interest.Niche)}: you have {string.Join(", ", interest.Apps)}"
            : interest.Learned ? $"You're into {Niches.Phrase(interest.Niche)}, learned from what you installed" : $"You're into {Niches.Phrase(interest.Niche)}, going by your developer tools";

    private static string Display(string trigger) => trigger.StartsWith('@') ? trigger[1..] + " packages" : trigger;

    private static IEnumerable<Recommendation> Tips(RecommendationContext context)
    {
        var tips = new List<Recommendation>();

        // The same program installed through two managers is updated (and can break) in two places.
        var duplicates = context.Installed
            .Where(a => !NotDuplicates.Contains(a.Name))
            .GroupBy(a => a.Name.Trim().ToLowerInvariant())
            .Where(g => g.Select(a => a.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count > 0 && !context.Dismissed.Contains("tip:duplicates"))
        {
            var lines = duplicates.Take(5).Select(g => $"{g.First().Name} ({string.Join(" and ", g.Select(a => a.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Order())})");
            tips.Add(new Recommendation("tip:duplicates", RecommendationKind.Tip, duplicates.Count == 1 ? "One program is installed twice" : $"{duplicates.Count} programs are installed twice",
                "Setup", "Each copy is updated separately, so one can fall behind or behave differently from the other.",
                "Keep the copy from the manager you trust most and remove the other: " + string.Join(", ", lines) + (duplicates.Count > 5 ? ", and more." : "."),
                null, null, 20));
        }

        var held = context.Installed.Where(a => a.IsPinned).Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (held.Count > 0 && !context.Dismissed.Contains("tip:held"))
            tips.Add(new Recommendation("tip:held", RecommendationKind.Tip, held.Count == 1 ? "One package is held back" : $"{held.Count} packages are held back",
                "Setup", "Held packages never update, including security fixes.",
                "Review them now and then: " + string.Join(", ", held.Take(6)) + (held.Count > 6 ? ", and more." : "."), null, null, 25));

        foreach (var manager in context.DisabledManagers)
        {
            var key = "tip:disabled:" + manager.ToLowerInvariant();
            if (context.Dismissed.Contains(key)) continue;
            tips.Add(new Recommendation(key, RecommendationKind.Tip, manager + " is installed but turned off", "Setup",
                $"Anything you installed with {manager} is not being checked for updates.",
                "Turn it on in Package managers, save, and scan again.", null, null, 30));
        }

        return tips.OrderByDescending(t => t.Priority);
    }

    private sealed record Inventory(HashSet<string> Names, HashSet<string> Providers)
    {
        public bool Matches(string trigger) => trigger.StartsWith('@') ? Providers.Contains(trigger[1..]) : Names.Contains(trigger);
    }

    private static Inventory Index(IReadOnlyList<InstalledApplication> installed)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in installed)
        {
            foreach (var key in AppKeys.Of(a)) names.Add(key);
            providers.Add(a.ProviderId.ToLowerInvariant());
        }
        return new Inventory(names, providers);
    }
}
