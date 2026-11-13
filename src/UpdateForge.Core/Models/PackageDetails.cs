using System.Text.RegularExpressions;

namespace UpdateForge.Core.Models;

public sealed record PackageDetailField(string Name, string Value);

/// <summary>Catalog metadata for one package, as reported by its manager. Every value is remote, untrusted text.</summary>
public sealed record PackageDetails(string? Description, Uri? Homepage, IReadOnlyList<PackageDetailField> Fields)
{
    private const int MaxFields = 80;
    private const int MaxValueLength = 4000;

    private static readonly string[] DescriptionKeys = ["Description", "Summary"];
    private static readonly string[] HomepageKeys = ["Homepage", "Website", "Software Site", "Publisher Url", "Package url"];

    /// <summary>
    /// Builds details from name/value pairs: picks the description and homepage, caps sizes,
    /// and only accepts http(s) homepages so a catalog entry can never make the app launch something else.
    /// </summary>
    public static PackageDetails From(IEnumerable<PackageDetailField> fields)
    {
        var list = fields
            .Select(f => new PackageDetailField(f.Name.Trim(), Clip(f.Value.Trim())))
            .Where(f => f.Name.Length > 0 && f.Value.Length > 0)
            .DistinctBy(f => (f.Name.ToUpperInvariant(), f.Value))
            .ToList();
        var description = DescriptionKeys.Select(k => list.FirstOrDefault(f => f.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is not null);
        Uri? homepage = null;
        foreach (var key in HomepageKeys)
        {
            var value = list.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
            if ((homepage = TryWebUri(value)) is not null) break;
        }
        if (description is not null) list.Remove(description);
        return new(description?.Value, homepage, list.Take(MaxFields).ToList());
    }

    public static Uri? TryWebUri(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;

    private static string Clip(string value) => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";
}

/// <summary>Parses the "Key: value" reports printed by <c>winget show</c>, <c>choco info</c> and <c>scoop info</c>.</summary>
public static partial class PackageDetailsText
{
    /// <param name="multiLineKeys">Keys whose following non-key lines belong to them even when they are not indented further.</param>
    /// <param name="splitPipes">Chocolatey prints several fields on one line separated by " | ".</param>
    public static IReadOnlyList<PackageDetailField> Parse(string output, IReadOnlyCollection<string>? multiLineKeys = null, bool splitPipes = false)
    {
        var fields = new List<(string Name, List<string> Lines, int Indent)>();
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // Spinner frames are written with carriage returns; keep only what was finally on the line.
            var line = raw.Contains('\r') ? raw[(raw.LastIndexOf('\r') + 1)..] : raw;
            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();
            var pairs = KeyValues(text, splitPipes);
            if (pairs.Count > 0)
            {
                foreach (var (name, value) in pairs) fields.Add((name, value.Length == 0 ? [] : [value], indent));
                continue;
            }
            if (fields.Count == 0) continue;
            var last = fields[^1];
            var open = indent > last.Indent
                || multiLineKeys?.Contains(last.Name, StringComparer.OrdinalIgnoreCase) == true;
            if (!open) continue;
            // Keep paragraph breaks inside descriptions, but never start a value with blank lines.
            if (text.Length > 0 || last.Lines.Count > 0) last.Lines.Add(text);
        }
        return [.. fields.Select(f => new PackageDetailField(f.Name, string.Join('\n', f.Lines).Trim()))];
    }

    private static List<(string, string)> KeyValues(string text, bool splitPipes)
    {
        if (splitPipes && text.Contains(" | ", StringComparison.Ordinal))
        {
            var parts = text.Split(" | ").Select(p => KeyValue(p)).ToList();
            if (parts.All(p => p is not null)) return [.. parts.Select(p => p!.Value)];
        }
        return KeyValue(text) is { } pair ? [pair] : [];
    }

    private static (string, string)? KeyValue(string text)
    {
        var m = KeyPattern().Match(text);
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null;
    }

    // A short label, then a colon followed by a space or the end of the line (so "https://..." is never a key).
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9 ()/._\-]{0,39}?)\s*:(?:\s+(.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
