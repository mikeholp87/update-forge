using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UpdateForge.Core.Bundles;

/// <summary>One package in a bundle. Only the WinGet identifier and source are ever used to install it.</summary>
public sealed record BundlePackage(string Id, string Source, string? Name = null, string? Version = null);

public sealed record PackageBundle(int SchemaVersion, DateTimeOffset CreatedAt, string CreatedBy, IReadOnlyList<BundlePackage> Packages);

/// <summary>Result of reading a bundle file: the valid packages plus a description of everything that was refused.</summary>
public sealed record BundleParseResult(PackageBundle? Bundle, IReadOnlyList<string> Rejected, string? Error)
{
    public bool Ok => Bundle is not null;
}

/// <summary>
/// The bundle file format. A bundle is untrusted input (it may come from another machine or another person), so it is parsed
/// defensively: size and count limits, a schema version check, and every identifier validated against the same strict patterns
/// the WinGet provider enforces before anything is installed. Invalid entries are dropped and reported, never sanitized.
/// </summary>
public static partial class PackageBundleFormat
{
    public const int CurrentSchema = 1;
    public const int MaxPackages = 2000;
    public const int MaxBytes = 2 * 1024 * 1024;
    public static readonly IReadOnlyList<string> AllowedSources = ["winget", "msstore"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);
    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);
    public static bool IsAllowedSource(string? source) => source is not null && AllowedSources.Contains(source, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(PackageBundle bundle) => JsonSerializer.Serialize(bundle, Options);

    public static BundleParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, [], "The bundle file is empty.");
        if (json.Length > MaxBytes) return new(null, [], $"The bundle file is larger than {MaxBytes / 1024 / 1024} MB; refusing to read it.");

        PackageBundle? raw;
        try { raw = JsonSerializer.Deserialize<PackageBundle>(json, Options); }
        catch (JsonException e) { return new(null, [], "The file is not a valid UpdateForge bundle: " + e.Message); }
        if (raw is null || raw.Packages is null) return new(null, [], "The file is not a valid UpdateForge bundle.");
        if (raw.SchemaVersion != CurrentSchema)
            return new(null, [], $"Unsupported bundle version {raw.SchemaVersion}. This version of UpdateForge reads version {CurrentSchema}.");
        if (raw.Packages.Count > MaxPackages) return new(null, [], $"The bundle lists more than {MaxPackages} packages; refusing to read it.");

        var rejected = new List<string>();
        var valid = new List<BundlePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in raw.Packages)
        {
            if (p is null || !IsValidId(p.Id)) { rejected.Add($"Invalid package id: {Printable(p?.Id)}"); continue; }
            if (!IsAllowedSource(p.Source)) { rejected.Add($"{p.Id}: source '{Printable(p.Source)}' is not allowed (only {string.Join(", ", AllowedSources)})."); continue; }
            if (p.Version is not null && !IsValidVersion(p.Version)) { rejected.Add($"{p.Id}: invalid version '{Printable(p.Version)}'."); continue; }
            if (!seen.Add(p.Id)) { rejected.Add($"{p.Id}: listed more than once."); continue; }
            valid.Add(p with { Source = p.Source.ToLowerInvariant(), Name = Clean(p.Name) });
        }
        return new(raw with { Packages = valid }, rejected, null);
    }

    // Names are shown to the user but never executed; strip control characters and bound the length.
    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var chars = name.Where(c => !char.IsControl(c)).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? null : s.Length > 120 ? s[..120] : s;
    }

    private static string Printable(string? s) => s is null ? "(missing)" : s.Length > 60 ? new string(s.Take(60).Where(c => !char.IsControl(c)).ToArray()) + "…" : new string(s.Where(c => !char.IsControl(c)).ToArray());
}
