using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.WinGet;

public sealed partial class WinGetProvider : IPackageSearchProvider
{
    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        // No source override: search every source configured in WinGet, including custom repositories.
        var result = await _runner.RunAsync(new ProcessSpec(_exe,
            ["search", "--query", query, .. CommonFlags], ScanTimeout), cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut && unchecked((uint)result.ExitCode) == 0x8A150014) return [];
        PackageManagerOutput.RequireSuccess(result, "Search WinGet packages");
        return ParsePackageSearch(result.StdOut);
    }

    /// <summary>Uses column positions, never localized column names or installed-package IDs.</summary>
    public static IReadOnlyList<PackageSearchResult> ParsePackageSearch(string output)
    {
        var packages = new List<PackageSearchResult>();
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        int[] columns = [];
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() is { Length: >= 3 } separator && separator.All(c => c == '-'))
            {
                var header = lines[i - 1];
                columns = SearchHeaderTokens().Matches(header).Select(m => DisplayWidth(header[..m.Index])).ToArray();
                continue;
            }
            if (columns.Length < 3) continue;
            string Cell(int index) => index >= columns.Length ? ""
                : line[IndexAtColumn(line, columns[index])..(index + 1 < columns.Length ? IndexAtColumn(line, columns[index + 1]) : line.Length)].Trim();
            var name = Cell(0);
            var id = Cell(1);
            var version = Cell(2);
            if (name.Length == 0 || !PackageIdPattern().IsMatch(id) || version.Length == 0) continue;
            // With a query across sources, the final column is Source; a single-source table may omit it.
            var last = Cell(columns.Length - 1);
            var source = columns.Length >= 5 || (columns.Length == 4 && !last.Contains(": ", StringComparison.Ordinal)) ? last : "";
            if (id.Contains('…') || id.Contains("...", StringComparison.Ordinal)) continue;
            packages.Add(new(ProviderKey, id, name, version, source.Length == 0 ? null : source));
        }
        return packages;
    }

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchHeaderTokens();

    // WinGet pads by terminal display columns, not UTF-16 length (e.g. Chinese Store names).
    private static int DisplayWidth(string text) => text.EnumerateRunes().Sum(RuneWidth);

    private static int IndexAtColumn(string text, int column)
    {
        var width = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (width >= column) break;
            width += RuneWidth(rune);
            index += rune.Utf16SequenceLength;
        }
        return index;
    }

    private static int RuneWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        var n = rune.Value;
        return n is >= 0x1100 and <= 0x115F or >= 0x2329 and <= 0x232A
            or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
    }
}
