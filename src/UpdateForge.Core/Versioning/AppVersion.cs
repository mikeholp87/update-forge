using System.Globalization;
using System.Text.RegularExpressions;

namespace UpdateForge.Core.Versioning;

public enum VersionOrder { Unknown = 0, Older, Equal, Newer }

/// <summary>
/// Conservative version comparison. Returns <see cref="VersionOrder.Unknown"/> whenever either side
/// cannot be parsed, so an unknown version is never reported as a confirmed newer version.
/// </summary>
public static partial class AppVersion
{
    [GeneratedRegex(@"^[vV]?(?<nums>\d+(?:\.\d+)*)(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out IReadOnlyList<ulong> numbers, out string? preRelease)
    {
        numbers = [];
        preRelease = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;

        var parts = new List<ulong>();
        foreach (var s in m.Groups["nums"].Value.Split('.'))
        {
            if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
            parts.Add(n);
        }
        numbers = parts;

        var tail = m.Groups["tail"].Value.Trim();
        if (tail.StartsWith('+')) tail = string.Empty; // build metadata is ignored
        tail = tail.TrimStart('-', '_', ' ');
        preRelease = tail.Length == 0 ? null : tail;
        return true;
    }

    /// <summary>Compares <paramref name="installed"/> against <paramref name="candidate"/>; the result describes installed relative to candidate.</summary>
    public static VersionOrder Compare(string? installed, string? candidate)
    {
        if (!TryParse(installed, out var a, out var preA) || !TryParse(candidate, out var b, out var preB))
            return VersionOrder.Unknown;

        var len = Math.Max(a.Count, b.Count);
        for (var i = 0; i < len; i++)
        {
            var x = i < a.Count ? a[i] : 0UL;
            var y = i < b.Count ? b[i] : 0UL;
            if (x != y) return x < y ? VersionOrder.Older : VersionOrder.Newer;
        }

        if (preA is null && preB is null) return VersionOrder.Equal;
        if (preA is null) return VersionOrder.Newer;   // release > prerelease
        if (preB is null) return VersionOrder.Older;
        var c = string.Compare(preA, preB, StringComparison.OrdinalIgnoreCase);
        return c == 0 ? VersionOrder.Equal : c < 0 ? VersionOrder.Older : VersionOrder.Newer;
    }

    /// <summary>True only when both versions parse and the candidate is strictly newer.</summary>
    public static bool IsNewer(string? installed, string? candidate) => Compare(installed, candidate) == VersionOrder.Older;
}
