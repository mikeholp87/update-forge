using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Processes;

namespace UpdateForge.Providers.Linux;

public static class PipxParsing
{
    /// <summary>Parses <c>pipx list --json</c>: one venv per app, with the main package's version under <c>metadata.main_package</c>.</summary>
    internal static IReadOnlyList<(string Name, string Version)> ParseList(string json)
    {
        using var doc = LinuxTools.ParseJson(json, '{') ?? throw new InvalidOperationException("pipx did not return a package list.");
        var list = new List<(string, string)>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("venvs", out var venvs) || venvs.ValueKind != JsonValueKind.Object) return list;
        foreach (var venv in venvs.EnumerateObject())
        {
            if (venv.Value.ValueKind == JsonValueKind.Object && venv.Value.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("main_package", out var main) && main.ValueKind == JsonValueKind.Object
                && main.TryGetProperty("package_version", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                list.Add((venv.Name, v.GetString()!));
        }
        return list;
    }

    /// <summary>Parses <c>pip list --outdated --format=json</c> into normalized name → latest version.</summary>
    internal static IReadOnlyDictionary<string, string> ParseOutdated(string json)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = LinuxTools.ParseJson(json, '[');
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return latest;
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && e.TryGetProperty("latest_version", out var l) && l.ValueKind == JsonValueKind.String)
                latest[Normalize(n.GetString()!)] = l.GetString()!;
        return latest;
    }

    /// <summary>PEP 503: runs of <c>-</c>, <c>_</c> and <c>.</c> are one separator, and case does not matter.</summary>
    internal static string Normalize(string name) => Regex.Replace(name, "[-_.]+", "-").ToLowerInvariant();
}

/// <summary>
/// Python applications installed with pipx. pipx has no outdated command, so each app's own pip is asked
/// (<c>pipx runpip &lt;app&gt; list --outdated</c>). Updates use <c>pipx upgrade</c>, which always moves to the newest release; pipx cannot
/// pin a version, and it has no catalog search (PyPI retired its search API).
/// </summary>
public sealed partial class PipxProvider : UserToolProvider
{
    public const string ProviderKey = "pipx";

    public PipxProvider(IProcessRunner runner, ILogger<PipxProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "pipx";
    internal override string ExeName => "pipx";
    internal override bool SupportsVersionPin => false;
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("NO_COLOR", "1"), ("PIP_DISABLE_PIP_VERSION_CHECK", "1"));
    internal override string SourcesFallback => "PyPI (pip's own index settings apply)";

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._\-]{0,98}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal override bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    internal override string[] InstallArgs(string name, string? version, string? source, bool update) =>
        update ? ["upgrade", name] : ["install", name];

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "list", "--json"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "List pipx apps");
        var packages = PipxParsing.ParseList(r.StdOut).Where(p => IsValidName(p.Name)).Select(p => new ToolPackage(p.Name, p.Version)).ToList();
        return checkUpdates ? await WithLatestAsync(packages, p => LatestAsync(p, cancellationToken), cancellationToken).ConfigureAwait(false) : packages;
    }

    private async Task<string?> LatestAsync(ToolPackage p, CancellationToken ct)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(3), null, "runpip", p.Name, "list", "--outdated", "--format=json"), ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0) return null;
        // pip lists only outdated packages, so absence means the app is current.
        return PipxParsing.ParseOutdated(r.StdOut).GetValueOrDefault(PipxParsing.Normalize(p.Name), p.Version);
    }
}
