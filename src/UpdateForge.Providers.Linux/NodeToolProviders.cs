using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;

namespace UpdateForge.Providers.Linux;

public static partial class NodeToolParsing
{
    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~\-]{0,213}/)?[a-z0-9][a-z0-9._~\-]{0,213}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    /// <summary>npm package names, scoped or not. Must start with a letter or digit, so a name can never be read as an option.</summary>
    internal static bool IsValidName(string? name) => name is { Length: <= 214 } && NamePattern().IsMatch(name);

    /// <summary>Versions that are not a registry release (<c>link:</c>, <c>file:</c>, <c>workspace:</c>, a git URL) mean the package is linked locally.</summary>
    internal static bool IsLinked(string version) => version.Contains(':');

    /// <summary>Parses <c>pnpm ls -g --depth=0 --json</c>: an array of projects, each with a <c>dependencies</c> map.</summary>
    internal static IReadOnlyList<(string Name, string Version)> ParsePnpmList(string json)
    {
        using var doc = LinuxTools.ParseJson(json, '[') ?? throw new InvalidOperationException("pnpm did not return a package list.");
        var list = new List<(string, string)>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var project in doc.RootElement.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var d in deps.EnumerateObject())
                if (d.Value.ValueKind == JsonValueKind.Object && d.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    list.Add((d.Name, v.GetString()!));
        }
        return list;
    }

    /// <summary>Parses <c>pnpm outdated -g --format json</c> into name → latest. An empty document means nothing is outdated.</summary>
    internal static IReadOnlyDictionary<string, string> ParsePnpmOutdated(string json)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = LinuxTools.ParseJson(json, '{');
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return latest;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(l.GetString())) latest[p.Name] = l.GetString()!;
        return latest;
    }

    /// <summary>
    /// Parses <c>bun pm ls -g</c>: a heading with the install folder, then <c>├── name@version</c> / <c>└── name@version</c> rows.
    /// Scoped names start with an @, so the version is whatever follows the last one.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Version)> ParseBunList(string output)
    {
        var list = new List<(string, string)>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var t = line.TrimStart();
            if (!t.StartsWith("├── ", StringComparison.Ordinal) && !t.StartsWith("└── ", StringComparison.Ordinal)) continue;
            var spec = t[4..].Trim();
            var at = spec.LastIndexOf('@');
            if (at > 0 && at < spec.Length - 1) list.Add((spec[..at], spec[(at + 1)..]));
        }
        return list;
    }

    /// <summary>
    /// Reads Yarn Classic's global folder: the <c>dependencies</c> of its package.json, with each installed version taken from
    /// <c>node_modules/&lt;name&gt;/package.json</c>. (<c>yarn global list</c> only shows packages that ship executables.)
    /// </summary>
    internal static IReadOnlyList<(string Name, string Version)> ReadYarnGlobal(string folder)
    {
        var manifest = Path.Combine(folder, "package.json");
        if (!File.Exists(manifest)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!doc.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object) return [];
        var list = new List<(string, string)>();
        foreach (var d in deps.EnumerateObject())
        {
            if (!IsValidName(d.Name)) continue;
            var installed = Path.Combine(folder, "node_modules", d.Name, "package.json");
            if (!File.Exists(installed)) continue;
            using var pkg = JsonDocument.Parse(File.ReadAllText(installed));
            if (pkg.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                list.Add((d.Name, v.GetString()!));
        }
        return list;
    }

    /// <summary>Reads the version from <c>yarn info name version --json</c> (<c>{"type":"inspect","data":"1.3.0"}</c>). Other lines are warnings.</summary>
    internal static string? ParseYarnInfo(string output)
    {
        foreach (var line in LinuxTools.Lines(output))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "inspect"
                    && doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
            }
            catch (JsonException) { /* not a JSON line */ }
        }
        return null;
    }

    internal static ToolPackage ToPackage(string name, string version, string tool) =>
        IsLinked(version)
            ? new ToolPackage(name, version, version, Pinned: true, Note: $"Linked from a local folder with {tool}; UpdateForge will not replace it.")
            : new ToolPackage(name, version);
}

/// <summary>pnpm global packages. <c>pnpm outdated -g</c> exits 1 when something is outdated, so the JSON decides, not the exit code.</summary>
public sealed class PnpmProvider : UserToolProvider
{
    public const string ProviderKey = "pnpm";

    public PnpmProvider(IProcessRunner runner, ILogger<PnpmProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "pnpm";
    internal override string ExeName => "pnpm";
    internal override bool SupportsPins => true;
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("COREPACK_ENABLE_DOWNLOAD_PROMPT", "0"), ("NO_COLOR", "1"), ("NPM_CONFIG_UPDATE_NOTIFIER", "false"));
    internal override string[] SourcesArgs => ["config", "get", "registry"];
    internal override bool IsValidName(string? name) => NodeToolParsing.IsValidName(name);

    internal override string[] InstallArgs(string name, string? version, string? source, bool update) =>
        ["add", "-g", version is null ? name : $"{name}@{version}"];

    internal override string? Hint(string output) =>
        output.Contains("ERR_PNPM_NO_GLOBAL_BIN_DIR", StringComparison.Ordinal) ? "pnpm has no global folder yet. Run `pnpm setup` once, then try again." : null;

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var ls = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "ls", "-g", "--depth=0", "--json"), cancellationToken).ConfigureAwait(false);
        if (ls.TimedOut) throw new TimeoutException("pnpm ls timed out.");
        var packages = NodeToolParsing.ParsePnpmList(ls.StdOut).Select(p => NodeToolParsing.ToPackage(p.Name, p.Version, "pnpm link")).ToList();
        if (!checkUpdates) return packages;

        var outdated = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(3), null, "outdated", "-g", "--format", "json"), cancellationToken).ConfigureAwait(false);
        if (outdated.TimedOut) throw new TimeoutException("pnpm outdated timed out.");
        if (outdated.ExitCode > 1 || (outdated.ExitCode == 1 && !outdated.StdOut.Contains('{')))
            throw new InvalidOperationException($"pnpm outdated failed (exit code {outdated.ExitCode}). {outdated.StdErr.Trim()}");
        var latest = NodeToolParsing.ParsePnpmOutdated(outdated.StdOut);
        return [.. packages.Select(p => p.Pinned ? p : p with { Latest = latest.GetValueOrDefault(p.Name, p.Version) })];
    }
}

/// <summary>
/// Bun global packages. <c>bun outdated</c> cannot target the global folder, so each package is looked up with <c>bun info</c>.
/// </summary>
public sealed class BunProvider : UserToolProvider
{
    public const string ProviderKey = "bun";

    public BunProvider(IProcessRunner runner, ILogger<BunProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "Bun";
    internal override string ExeName => "bun";
    internal override bool SupportsPins => true;
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("NO_COLOR", "1"));
    internal override string SourcesFallback => "npm registry (configure in ~/.bunfig.toml or ~/.npmrc)";
    internal override bool IsValidName(string? name) => NodeToolParsing.IsValidName(name);

    internal override string[] InstallArgs(string name, string? version, string? source, bool update) =>
        ["add", "-g", version is null ? name : $"{name}@{version}"];

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "pm", "ls", "-g"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "List Bun packages");
        var packages = NodeToolParsing.ParseBunList(r.StdOut).Select(p => NodeToolParsing.ToPackage(p.Name, p.Version, "bun link")).ToList();
        return checkUpdates ? await WithLatestAsync(packages, p => LatestAsync(p, cancellationToken), cancellationToken).ConfigureAwait(false) : packages;
    }

    private async Task<string?> LatestAsync(ToolPackage p, CancellationToken ct)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "info", p.Name, "version"), ct).ConfigureAwait(false);
        var version = LinuxTools.Lines(r.StdOut).FirstOrDefault()?.Trim();
        return r.TimedOut || r.ExitCode != 0 || !IsValidVersion(version) ? null : version;
    }
}

/// <summary>
/// Yarn Classic (1.x) global packages. Yarn 2 and later have no global installs, so they are reported as unsupported.
/// </summary>
public sealed class YarnProvider : UserToolProvider
{
    public const string ProviderKey = "yarn";

    public YarnProvider(IProcessRunner runner, ILogger<YarnProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "Yarn";
    internal override string ExeName => "yarn";
    internal override bool SupportsPins => true;
    // Corepack would otherwise stop at a download confirmation that nothing can answer.
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("COREPACK_ENABLE_DOWNLOAD_PROMPT", "0"), ("NO_UPDATE_NOTIFIER", "1"));
    internal override string[] SourcesArgs => ["config", "get", "registry"];
    internal override bool IsValidName(string? name) => NodeToolParsing.IsValidName(name);

    internal override string? VersionProblem(string versionLine) =>
        versionLine.StartsWith("1.", StringComparison.Ordinal) ? null : "Only Yarn Classic (1.x) supports global packages; this Yarn is version " + versionLine + ".";

    internal override string[] InstallArgs(string name, string? version, string? source, bool update) =>
        ["global", "add", version is null ? name : $"{name}@{version}"];

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var dir = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "global", "dir"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(dir, "Find the Yarn global folder");
        var folder = LinuxTools.Lines(dir.StdOut).LastOrDefault()?.Trim();
        if (folder is null || !Path.IsPathRooted(folder)) throw new InvalidOperationException("Yarn did not report its global folder.");
        var packages = NodeToolParsing.ReadYarnGlobal(folder).Select(p => NodeToolParsing.ToPackage(p.Name, p.Version, "yarn link")).ToList();
        return checkUpdates ? await WithLatestAsync(packages, p => LatestAsync(p, cancellationToken), cancellationToken).ConfigureAwait(false) : packages;
    }

    private async Task<string?> LatestAsync(ToolPackage p, CancellationToken ct)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "info", p.Name, "version", "--json"), ct).ConfigureAwait(false);
        var version = r.TimedOut || r.ExitCode != 0 ? null : NodeToolParsing.ParseYarnInfo(r.StdOut);
        return IsValidVersion(version) ? version : null;
    }
}
