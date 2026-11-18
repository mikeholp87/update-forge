using System.Text.RegularExpressions;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Recommendations;

namespace UpdateForge.Providers.Linux;

/// <summary>One installed package and what it recommends or suggests.</summary>
public sealed record AptRelationRow(string Name, IReadOnlyList<string> Recommends, IReadOnlyList<string> Suggests);

/// <summary>The fields of an <c>apt-cache show</c> stanza that decide whether a package is an application worth suggesting.</summary>
public sealed record AptShown(string Name, string Section, string Summary, IReadOnlyList<string> Tags);

public static partial class AptRelations
{
    /// <summary>Package names in a dependency field (<c>a (&gt;= 1), b | c, d:any</c>): version constraints and architecture qualifiers are dropped, alternatives kept.</summary>
    public static IReadOnlyList<string> ParseNames(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return [];
        var names = new List<string>();
        foreach (var part in field.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part;
            var cut = name.IndexOfAny([' ', '(', ':', '<']);
            if (cut >= 0) name = name[..cut];
            if (AptProvider.IsValidName(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Parses <c>dpkg-query -W -f='${Package}\t${db:Status-Abbrev}\t${Recommends}\t${Suggests}\n'</c>; only fully installed packages (<c>ii</c>) are returned.
    /// </summary>
    public static IReadOnlyList<AptRelationRow> ParseInstalled(string output)
    {
        var rows = new List<AptRelationRow>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length < 2 || !f[1].StartsWith("ii", StringComparison.Ordinal) || f[0].Length == 0) continue;
            rows.Add(new AptRelationRow(f[0], ParseNames(f.Length > 2 ? f[2] : null), ParseNames(f.Length > 3 ? f[3] : null)));
        }
        return rows;
    }

    /// <summary>Parses <c>apt-cache show --no-all-versions</c> output (blank-line separated stanzas; folded fields continue on indented lines).</summary>
    public static IReadOnlyList<AptShown> ParseShow(string output)
    {
        var result = new List<AptShown>();
        string? name = null, section = null, summary = null;
        var tags = new List<string>();
        string? field = null;

        void Flush()
        {
            if (name is not null) result.Add(new AptShown(name, section ?? "", DiscoveryText.Clean(summary, 140), [.. tags]));
            name = section = summary = field = null;
            tags.Clear();
        }

        foreach (var raw in output.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0) { Flush(); continue; }
            if (char.IsWhiteSpace(raw[0]))
            {
                if (field == "Tag") tags.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var key = raw[..colon];
            var value = raw[(colon + 1)..].Trim();
            field = key.StartsWith("Description", StringComparison.Ordinal) ? "Description" : key;
            switch (field)
            {
                case "Package": if (name is not null) Flush(); name = value; field = "Package"; break;
                case "Section": section = value; break;
                case "Description": summary ??= value; break;
                case "Tag": tags.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
            }
        }
        Flush();
        return result;
    }

    private static readonly HashSet<string> ApplicationSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "games", "graphics", "sound", "video", "net", "mail", "editors", "text", "tex", "web", "science", "math", "electronics", "hamradio", "utils",
        "admin", "devel", "vcs", "shells", "comm", "database", "gnome", "kde", "x11", "news", "embedded", "otherosfs",
    };

    /// <summary>False for libraries, documentation, data, fonts and the like, going by the package name alone.</summary>
    public static bool LooksLikeAppName(string name) => AptProvider.IsValidName(name) && !NotAnApp().IsMatch(name);

    /// <summary>
    /// Libraries, documentation, data, kernels, daemons and the like are never suggested. When Debian's role tags are present they decide
    /// (a program, and not a background service); otherwise the section does.
    /// </summary>
    public static bool LooksLikeApplication(AptShown p)
    {
        if (!LooksLikeAppName(p.Name)) return false;
        var section = p.Section;
        var slash = section.LastIndexOf('/');
        if (slash >= 0) section = section[(slash + 1)..];
        if (!ApplicationSections.Contains(section)) return false;
        return p.Tags.Count == 0 || (p.Tags.Contains("role::program") && !p.Tags.Contains("interface::daemon"));
    }

    [GeneratedRegex(@"^(lib|python3?-|node-|golang-|ruby-|php|r-|fonts-|gir1\.2-|linux-|firmware-|xserver-xorg|task-|language-pack|libreoffice-l10n|hunspell|hyphen|mythes)|(-doc|-docs|-dev|-dbg|-dbgsym|-data|-common|-l10n|-examples|-locale|-i18n)$|-doc-",
        RegexOptions.CultureInvariant)]
    private static partial Regex NotAnApp();
}

/// <summary>
/// Looks through this computer's own apt metadata for programs that something you deliberately installed recommends or suggests but you
/// do not have. It runs three local read-only commands, contacts nothing, and ranks by how many of your packages point at a candidate.
/// </summary>
public sealed class AptRelationDiscovery(IProcessRunner runner, string? binDirectory = null) : IDiscoverySource
{
    private const int MaxCandidates = 60, MaxResults = 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool RequiresNetwork => false;

    public async Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(DiscoveryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mark = LinuxTools.Find("apt-mark", binDirectory);
        var query = LinuxTools.Find("dpkg-query", binDirectory);
        var cache = LinuxTools.Find("apt-cache", binDirectory);
        if (mark is null || query is null || cache is null) return [];

        var manual = await RunAsync(mark, ["showmanual"], ct).ConfigureAwait(false);
        var installed = await RunAsync(query, ["-W", "-f=${Package}\t${db:Status-Abbrev}\t${Recommends}\t${Suggests}\n"], ct).ConfigureAwait(false);
        if (manual is null || installed is null) return [];

        var chosen = AptParsing.ParseNames(manual).Select(n => n.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
        var rows = AptRelations.ParseInstalled(installed);
        var have = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        // Candidate name to (weight, the packages you chose that point at it). A recommendation counts twice as much as a suggestion.
        var candidates = new Dictionary<string, (int Weight, SortedSet<string> From)>(StringComparer.Ordinal);
        // Libraries and the like are sometimes marked as chosen too; what they suggest says nothing about what the user wants.
        foreach (var row in rows.Where(r => chosen.Contains(r.Name) && AptRelations.LooksLikeAppName(r.Name)))
        {
            foreach (var (names, weight) in new[] { (row.Recommends, 2), (row.Suggests, 1) })
                foreach (var name in names.Where(n => !have.Contains(n)))
                {
                    var entry = candidates.GetValueOrDefault(name, (0, new SortedSet<string>(StringComparer.Ordinal)));
                    entry.Item2.Add(row.Name);
                    candidates[name] = (entry.Item1 + weight, entry.Item2);
                }
        }
        if (candidates.Count == 0) return [];

        var top = candidates.OrderByDescending(c => c.Value.Weight).ThenBy(c => c.Key, StringComparer.Ordinal).Take(MaxCandidates).Select(c => c.Key).ToList();
        var shown = await RunAsync(cache, ["show", "--no-all-versions", "--", .. top], ct).ConfigureAwait(false);
        if (shown is null) return [];

        return [.. AptRelations.ParseShow(shown)
            .Where(AptRelations.LooksLikeApplication)
            .Where(p => candidates.ContainsKey(p.Name))
            .OrderByDescending(p => candidates[p.Name].Weight).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(p => new DiscoveredPackage("apt", p.Name, p.Name, p.Summary, CategoryNiches.FromAptSection(p.Section), DiscoveryOrigin.Related,
                [.. candidates[p.Name].From.Take(4)]))];
    }

    /// <summary>Standard output, or null when the tool failed to start or timed out. A non-zero exit still returns output: apt-cache complains about virtual packages but lists the rest.</summary>
    private async Task<string?> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync(new ProcessSpec(exe, args, Timeout, null, LinuxTools.CLocale), ct).ConfigureAwait(false);
            return r.TimedOut || (r.ExitCode != 0 && r.StdOut.Length == 0) ? null : r.StdOut;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
