using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Linux;

public static class HomebrewParsing
{
    /// <summary>Parses <c>brew list --versions</c> (<c>name 1.0 1.1</c>); when several versions are kept, the last one is reported.</summary>
    internal static IReadOnlyDictionary<string, string> ParseVersions(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in LinuxTools.Lines(output))
        {
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 2 && HomebrewProvider.IsValidBrewName(t[0])) map[t[0]] = t[^1];
        }
        return map;
    }

    /// <summary>Parses a plain list of names, one per line (<c>brew leaves</c>, <c>brew list --pinned</c>, <c>brew search</c>). Anything else is skipped.</summary>
    internal static IReadOnlyList<string> ParseNames(string output) =>
        [.. LinuxTools.Lines(output).Select(l => l.Trim()).Where(HomebrewProvider.IsValidBrewName)];

    /// <summary>Parses <c>brew outdated --json=v2</c> into formula and cask name → newest version.</summary>
    internal static (IReadOnlyDictionary<string, string> Formulae, IReadOnlyDictionary<string, string> Casks) ParseOutdated(string json)
    {
        using var doc = LinuxTools.ParseJson(json, '{') ?? throw new InvalidOperationException("brew did not return an outdated list.");
        return (Read(doc.RootElement, "formulae"), Read(doc.RootElement, "casks"));

        static Dictionary<string, string> Read(JsonElement root, string key)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && e.TryGetProperty("current_version", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    map[n.GetString()!] = v.GetString()!;
            return map;
        }
    }

    /// <summary>Parses <c>brew info --json=v2</c> for one formula or cask.</summary>
    internal static PackageDetails ParseInfo(string json)
    {
        using var doc = LinuxTools.ParseJson(json, '{') ?? throw new InvalidOperationException("brew did not return package details.");
        var fields = new List<PackageDetailField>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(name, value)); }
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        foreach (var key in (string[])["formulae", "casks"])
        {
            if (!doc.RootElement.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) continue;
            var e = arr[0];
            Add("Description", Text(e, "desc"));
            Add("Homepage", Text(e, "homepage"));
            Add("Latest version", e.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object ? Text(vs, "stable") : Text(e, "version"));
            Add("License", Text(e, "license"));
            Add("Tap", Text(e, "tap"));
            if (e.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
                Add("Dependencies", deps.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture));
            break;
        }
        return PackageDetails.From(fields);
    }
}

/// <summary>
/// Homebrew on Linux (Linuxbrew). Inventory is the formulae installed on request (<c>brew leaves</c>) plus anything outdated, and
/// installed casks, so the dozens of dependencies Homebrew pulled in do not drown the list. Homebrew refuses to run as root and
/// cannot install an arbitrary version, so updates always move to the current one. "Refresh sources" runs <c>brew update</c>.
/// </summary>
public sealed partial class HomebrewProvider : UserToolProvider, IPackageSearchProvider
{
    public const string ProviderKey = "brew";
    private const string Formula = "formula";
    private const string Cask = "cask";

    public HomebrewProvider(IProcessRunner runner, ILogger<HomebrewProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "Homebrew";
    internal override string ExeName => "brew";
    public override bool CanRefreshSources => true;
    internal override bool SupportsPins => true;
    internal override bool SupportsVersionPin => false;
    internal override int InstallTimeoutMinutes => 60;
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("NO_COLOR", "1"), ("HOMEBREW_NO_ANALYTICS", "1"), ("HOMEBREW_NO_ENV_HINTS", "1"), ("HOMEBREW_NO_EMOJI", "1"));
    internal override string[] SourcesArgs => ["tap"];

    // Optional "owner/tap/" prefix, then a name that starts with a letter or digit so it can never be read as an option or a path.
    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*/)?[A-Za-z0-9][A-Za-z0-9+._@\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal static bool IsValidBrewName(string? name) => name is not null && NamePattern().IsMatch(name);
    internal override bool IsValidName(string? name) => IsValidBrewName(name);

    internal override string? Hint(string output) =>
        output.Contains("as root", StringComparison.OrdinalIgnoreCase) ? "Homebrew will not run as root. Start UpdateForge as your normal user." : null;

    internal override string[]? InstallArgs(string name, string? version, string? source, bool update)
    {
        var kind = source switch { null or Formula => "--formula", Cask => "--cask", _ => null };
        return kind is null ? null : [update ? "upgrade" : "install", kind, name];
    }

    public override async Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await Runner.RunAsync(Spec(TimeSpan.FromMinutes(10), null, "update"), cancellationToken).ConfigureAwait(false), "Refresh Homebrew");

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var formulae = HomebrewParsing.ParseVersions(PackageManagerOutput.RequireSuccess(
            await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "list", "--formula", "--versions"), cancellationToken).ConfigureAwait(false), "List Homebrew formulae"));

        // Casks are not available on every Linuxbrew version; a failure here only means there are none to show.
        var caskRun = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "list", "--cask", "--versions"), cancellationToken).ConfigureAwait(false);
        var casks = caskRun is { ExitCode: 0, TimedOut: false } ? HomebrewParsing.ParseVersions(caskRun.StdOut) : new Dictionary<string, string>();

        if (!checkUpdates)
            return [.. formulae.Select(f => new ToolPackage(f.Key, f.Value, Source: Formula)),
                    .. casks.Select(c => new ToolPackage(c.Key, c.Value, Source: Cask, Key: "cask:" + c.Key))];

        var leaves = HomebrewParsing.ParseNames(PackageManagerOutput.RequireSuccess(
            await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "leaves"), cancellationToken).ConfigureAwait(false), "List Homebrew formulae installed on request")).ToHashSet(StringComparer.Ordinal);
        var pinnedRun = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "list", "--pinned"), cancellationToken).ConfigureAwait(false);
        var pinned = pinnedRun.ExitCode == 0 ? HomebrewParsing.ParseNames(pinnedRun.StdOut).ToHashSet(StringComparer.Ordinal) : [];

        // brew outdated may run an automatic `brew update` first, which can take a while.
        var (outdatedFormulae, outdatedCasks) = HomebrewParsing.ParseOutdated(PackageManagerOutput.RequireSuccess(
            await Runner.RunAsync(Spec(TimeSpan.FromMinutes(10), null, "outdated", "--json=v2"), cancellationToken).ConfigureAwait(false), "Check Homebrew for updates"));

        var result = new List<ToolPackage>();
        foreach (var (name, version) in formulae.Where(f => leaves.Contains(f.Key) || outdatedFormulae.ContainsKey(f.Key) || pinned.Contains(f.Key)))
        {
            var isPinned = pinned.Contains(name);
            result.Add(new ToolPackage(name, version, outdatedFormulae.GetValueOrDefault(name, version), isPinned, Source: Formula,
                Note: isPinned ? "Pinned with brew pin; UpdateForge will not upgrade it." : null));
        }
        result.AddRange(casks.Select(c => new ToolPackage(c.Key, c.Value, outdatedCasks.GetValueOrDefault(c.Key, c.Value), Source: Cask, Key: "cask:" + c.Key)));
        return result;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var results = new List<PackageSearchResult>();
        foreach (var kind in (string[])[Formula, Cask])
        {
            var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "search", "--" + kind, "--", query), cancellationToken).ConfigureAwait(false);
            // brew search exits 1 with "No formulae or casks found" when nothing matches; that is an empty result, not a failure.
            if (r.ExitCode != 0 && r.StdOut.Trim().Length == 0 && r.StdErr.Contains("No ", StringComparison.Ordinal)) continue;
            PackageManagerOutput.RequireSuccess(r, "Search Homebrew");
            results.AddRange(HomebrewParsing.ParseNames(r.StdOut).Take(20).Select(n => new PackageSearchResult(ProviderKey, n, n, "Unknown", kind)));
        }
        return results;
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidBrewName(package.PackageId) || package.Source is not (Formula or Cask)) throw new ArgumentException("Homebrew package failed validation.", nameof(package));
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "info", "--json=v2", "--" + package.Source, package.PackageId), cancellationToken).ConfigureAwait(false);
        return HomebrewParsing.ParseInfo(PackageManagerOutput.RequireSuccess(r, "Read Homebrew package details"));
    }
}
