using System.Text.Json;

namespace UpdateForge.Providers.WinGet;

public sealed record WinGetExportedPackage(string Id, string Version, string Source);

public sealed record WinGetRow(string Name, string Id, string Version, string? Available, string? Source);

/// <summary>
/// WinGet's table output is localized, so headers are never parsed. Instead rows are anchored on package
/// identifiers obtained from the locale-independent <c>winget export</c> JSON: the text before the identifier
/// is the name, the tokens after it are versions and (optionally) the source.
/// </summary>
public static class WinGetParsing
{
    public static IReadOnlyList<WinGetExportedPackage> ParseExport(string json)
    {
        var list = new List<WinGetExportedPackage>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Sources", out var sources)) return list;
        foreach (var source in sources.EnumerateArray())
        {
            var sourceName = source.TryGetProperty("SourceDetails", out var sd) && sd.TryGetProperty("Name", out var n)
                ? n.GetString() ?? string.Empty : string.Empty;
            if (!source.TryGetProperty("Packages", out var packages)) continue;
            foreach (var p in packages.EnumerateArray())
            {
                var id = p.TryGetProperty("PackageIdentifier", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var version = p.TryGetProperty("Version", out var v) ? v.GetString() ?? "Unknown" : "Unknown";
                list.Add(new WinGetExportedPackage(id, version, sourceName));
            }
        }
        return list;
    }

    public static bool IsLocalId(string token) =>
        token.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) || token.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<WinGetRow> ParseRows(string output, IReadOnlySet<string> knownIds, IReadOnlySet<string> knownSources)
    {
        var rows = new List<WinGetRow>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var tokens = Tokenize(line);
            for (var i = 1; i < tokens.Count; i++)
            {
                var (text, start) = tokens[i];
                if (!knownIds.Contains(text) && !IsLocalId(text)) continue;

                var name = line[..start].Trim();
                var rest = MergeComparators(tokens.Skip(i + 1).Select(t => t.Text).ToList());
                string? source = null;
                if (rest.Count > 0 && knownSources.Contains(rest[^1])) { source = rest[^1]; rest.RemoveAt(rest.Count - 1); }
                if (rest.Count == 0 || name.Length == 0) break;
                rows.Add(new WinGetRow(name, text, rest[0], rest.Count > 1 ? rest[1] : null, source));
                break;
            }
        }
        return rows;
    }

    private static List<(string Text, int Start)> Tokenize(string line)
    {
        var tokens = new List<(string, int)>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            if (i > start) tokens.Add((line[start..i], start));
        }
        return tokens;
    }

    private static List<string> MergeComparators(List<string> tokens)
    {
        var merged = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "<" or ">" && i + 1 < tokens.Count) { merged.Add(tokens[i] + " " + tokens[i + 1]); i++; }
            else merged.Add(tokens[i]);
        }
        return merged;
    }
}
