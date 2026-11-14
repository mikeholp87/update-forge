using System.Numerics;

namespace UpdateForge.Core.Versioning;

/// <summary>Debian policy version ordering (epoch:upstream-revision, with <c>~</c> sorting before everything). Used for apt packages.</summary>
public static class DebianVersion
{
    public static VersionOrder Compare(string? installed, string? candidate)
    {
        if (!TrySplit(installed, out var ea, out var ua, out var ra) || !TrySplit(candidate, out var eb, out var ub, out var rb))
            return VersionOrder.Unknown;
        var c = ea.CompareTo(eb);
        if (c == 0) c = Part(ua, ub);
        if (c == 0) c = Part(ra, rb);
        return c == 0 ? VersionOrder.Equal : c < 0 ? VersionOrder.Older : VersionOrder.Newer;
    }

    private static bool TrySplit(string? v, out BigInteger epoch, out string upstream, out string revision)
    {
        (epoch, upstream, revision) = (0, "", "");
        v = v?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        var colon = v.IndexOf(':');
        if (colon >= 0)
        {
            if (!BigInteger.TryParse(v[..colon], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out epoch)) return false;
            v = v[(colon + 1)..];
        }
        var dash = v.LastIndexOf('-');
        (upstream, revision) = dash >= 0 ? (v[..dash], v[(dash + 1)..]) : (v, "");
        return upstream.Length > 0 && char.IsAsciiDigit(upstream[0]);
    }

    private static int Order(char c) => c switch
    {
        '~' => -1,
        _ when char.IsAsciiDigit(c) => 0,
        _ when char.IsAsciiLetter(c) => c,
        _ => c + 256,
    };

    private static int Part(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            for (; (i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])); i++, j++)
            {
                var d = (i < a.Length ? Order(a[i]) : 0) - (j < b.Length ? Order(b[j]) : 0);
                if (d != 0) return d < 0 ? -1 : 1;
            }
            int si = i, sj = j;
            while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
            while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
            var x = si == i ? BigInteger.Zero : BigInteger.Parse(a[si..i], System.Globalization.CultureInfo.InvariantCulture);
            var y = sj == j ? BigInteger.Zero : BigInteger.Parse(b[sj..j], System.Globalization.CultureInfo.InvariantCulture);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
}
