using System.Text;
using System.Text.RegularExpressions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Core.Inventory;

/// <summary>
/// Merges per-provider inventories into one list. Registry entries are inventory-only: they are folded into a
/// package-manager entry only when exactly one candidate matches on strict criteria. Ambiguity is never resolved
/// by guessing; the entries stay separate and keep their own (lower) confidence.
/// </summary>
public static partial class InventoryMerger
{
    public const string RegistryProviderId = "registry";

    /// <summary>Providers that only report what is installed (no update capability); folded into package-manager entries when unambiguous.</summary>
    public static bool IsInventoryOnly(string providerId) => providerId is RegistryProviderId or "msix";

    [GeneratedRegex(@"\((?:[^)]*(?:bit|x64|x86|arm64|amd64)[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchTag();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();

    public static string NormalizeName(string? name, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var s = ArchTag().Replace(name, " ");
        if (!string.IsNullOrWhiteSpace(version)) s = s.Replace(version, " ", StringComparison.OrdinalIgnoreCase);
        return NonAlnum().Replace(s, " ").Trim().ToLowerInvariant();
    }

    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var token in NonAlnum().Replace(publisher, " ").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "inc" or "llc" or "ltd" or "corp" or "corporation" or "gmbh" or "co" or "limited") continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(token);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<InstalledApplication> Merge(IEnumerable<InstalledApplication> all)
    {
        var list = all.ToList();

        // 1. Exact duplicates: same provider + package id + scope collapse into one entry.
        var primaries = new List<InstalledApplication>();
        var inventoryOnly = new List<InstalledApplication>();
        foreach (var app in list)
        {
            if (IsInventoryOnly(app.ProviderId)) { inventoryOnly.Add(app); continue; }
            var idx = primaries.FindIndex(p => p.ProviderId == app.ProviderId && p.PackageId is not null
                && string.Equals(p.PackageId, app.PackageId, StringComparison.OrdinalIgnoreCase) && p.Scope == app.Scope);
            if (idx < 0) primaries.Add(app);
            else primaries[idx] = primaries[idx] with { Evidence = [.. primaries[idx].Evidence, .. app.Evidence] };
        }

        // 2. Fold registry entries into at most one primary when the match is unambiguous.
        var matches = new Dictionary<InstalledApplication, List<InstalledApplication>>();
        foreach (var reg in inventoryOnly)
            matches[reg] = primaries.Where(p => IsSameInstall(reg, p)).ToList();

        var folded = new HashSet<InstalledApplication>();
        for (var i = 0; i < primaries.Count; i++)
        {
            var p = primaries[i];
            var candidates = inventoryOnly.Where(r => matches[r].Contains(p) && matches[r].Count == 1).ToList();
            if (candidates.Count != 1) continue; // none, or ambiguous between several registry rows
            var r = candidates[0];
            folded.Add(r);
            primaries[i] = p with
            {
                Publisher = p.Publisher ?? r.Publisher,
                InstallLocation = p.InstallLocation ?? r.InstallLocation,
                ProductCode = p.ProductCode ?? r.ProductCode,
                Architecture = p.Architecture ?? r.Architecture,
                // Link quality is recorded as evidence; the package keeps its own (authoritative) identity confidence.
                Evidence = [.. p.Evidence, .. r.Evidence.Select(e => e with { Detail = $"registry link: {Confidence(r, p)}" })],
            };
        }

        var result = new List<InstalledApplication>(primaries);
        foreach (var r in inventoryOnly.Where(r => !folded.Contains(r)))
        {
            var ambiguous = matches[r].Count > 1;
            result.Add(ambiguous ? r with { Confidence = IdentityConfidence.Low, Note = "Matches several packages; not merged." } : r);
        }
        return result;
    }

    private static bool IsSameInstall(InstalledApplication registry, InstalledApplication package)
    {
        if (!string.IsNullOrEmpty(registry.ProductCode) && string.Equals(registry.ProductCode, package.ProductCode, StringComparison.OrdinalIgnoreCase))
            return true;

        var nameR = NormalizeName(registry.Name, registry.InstalledVersion);
        var nameP = NormalizeName(package.Name, package.InstalledVersion);
        if (nameR.Length == 0 || nameR != nameP) return false;

        if (!VersionsCompatible(registry.InstalledVersion, package.InstalledVersion)) return false;
        if (registry.Scope != InstallScope.Unknown && package.Scope != InstallScope.Unknown && registry.Scope != package.Scope) return false;

        var pubR = NormalizePublisher(registry.Publisher);
        var pubP = NormalizePublisher(package.Publisher);
        return pubR.Length == 0 || pubP.Length == 0 || pubR == pubP;
    }

    private static bool VersionsCompatible(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ||
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ||
        AppVersion.Compare(a, b) == VersionOrder.Equal;

    private static IdentityConfidence Confidence(InstalledApplication registry, InstalledApplication package)
    {
        if (!string.IsNullOrEmpty(registry.ProductCode) && string.Equals(registry.ProductCode, package.ProductCode, StringComparison.OrdinalIgnoreCase))
            return IdentityConfidence.Exact;
        var pubKnown = NormalizePublisher(registry.Publisher).Length > 0 && NormalizePublisher(package.Publisher).Length > 0;
        var versionKnown = !string.IsNullOrWhiteSpace(registry.InstalledVersion) && !string.IsNullOrWhiteSpace(package.InstalledVersion);
        return pubKnown && versionKnown ? IdentityConfidence.High : IdentityConfidence.Medium;
    }
}
