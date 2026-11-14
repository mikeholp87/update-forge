using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Linux;

public static partial class CargoParsing
{
    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9_\-]*) v(?<version>\S+?)(?: \((?<source>.+)\))?:$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9_\-]*) = ""(?<version>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SearchPattern();

    /// <summary>
    /// Parses <c>cargo install --list</c>: an unindented <c>name vX.Y.Z:</c> line per crate (path and git installs add the origin in
    /// parentheses) followed by indented binary names. A null source means the crate came from crates.io.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Version, string? Source)> ParseInstallList(string output)
    {
        var list = new List<(string, string, string?)>();
        foreach (var line in LinuxTools.Lines(output))
        {
            if (char.IsWhiteSpace(line[0])) continue;
            var m = HeaderPattern().Match(line);
            if (m.Success && CargoProvider.IsValidCrate(m.Groups["name"].Value))
                list.Add((m.Groups["name"].Value, m.Groups["version"].Value, m.Groups["source"].Success ? m.Groups["source"].Value : null));
        }
        return list;
    }

    /// <summary>Parses <c>cargo search</c> lines (<c>name = "1.2.3"    # description</c>); notices and the "... and N crates more" footer are skipped.</summary>
    internal static IReadOnlyList<(string Name, string Version)> ParseSearch(string output)
    {
        var list = new List<(string, string)>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var m = SearchPattern().Match(line);
            if (m.Success) list.Add((m.Groups["name"].Value, m.Groups["version"].Value));
        }
        return list;
    }

    /// <summary>crates.io treats <c>-</c> and <c>_</c> as the same character and ignores case.</summary>
    internal static string Normalize(string name) => name.Replace('_', '-').ToLowerInvariant();

    /// <summary>Parses <c>cargo info</c>: a header, a description, then <c>key: value</c> lines.</summary>
    internal static PackageDetails ParseInfo(string output)
    {
        var lines = LinuxTools.Lines(output).ToList();
        var firstField = lines.FindIndex(l => l.StartsWith("version:", StringComparison.Ordinal));
        var fields = new List<PackageDetailField>();
        if (firstField > 1) fields.Add(new("Description", string.Join(' ', lines.Skip(1).Take(firstField - 1).Select(l => l.Trim()))));
        if (firstField >= 0) fields.AddRange(PackageDetailsText.Parse(string.Join('\n', lines.Skip(firstField))));
        return PackageDetails.From(fields);
    }
}

/// <summary>
/// Crates installed with <c>cargo install</c>. Cargo has no outdated check of its own, so each crates.io crate is looked up with
/// <c>cargo search</c>. Crates installed from a path, a git URL or another registry are reported as pinned and never replaced.
/// An update recompiles the crate, so it needs a C toolchain and can take minutes.
/// </summary>
public sealed partial class CargoProvider : UserToolProvider, IPackageSearchProvider
{
    public const string ProviderKey = "cargo";

    public CargoProvider(IProcessRunner runner, ILogger<CargoProvider> log, string? binDirectory = null) : base(runner, log, binDirectory) { }

    public override string Id => ProviderKey;
    public override string DisplayName => "Cargo";
    internal override string ExeName => "cargo";
    internal override bool SupportsPins => true;
    internal override int InstallTimeoutMinutes => 60;
    internal override IReadOnlyDictionary<string, string> Env { get; } = LinuxTools.WithEnv(("CARGO_TERM_COLOR", "never"), ("CARGO_TERM_PROGRESS_WHEN", "never"));
    internal override string SourcesFallback => "crates.io (alternative registries are configured in ~/.cargo/config.toml)";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal static bool IsValidCrate(string? name) => name is not null && NamePattern().IsMatch(name);
    internal override bool IsValidName(string? name) => IsValidCrate(name);

    internal override string[]? InstallArgs(string name, string? version, string? source, bool update) =>
        // "=" pins the exact version that was checked; a bare number would be read as a caret requirement.
        version is null ? ["install", name] : ["install", name, "--version", "=" + version];

    internal override string? Hint(string output) =>
        output.Contains("linker `cc` not found", StringComparison.Ordinal) || output.Contains("is `cc` installed", StringComparison.Ordinal)
            ? "Cargo needs a C compiler to build this crate. Install build-essential (or your distribution's equivalent)."
            : null;

    internal override async Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "install", "--list"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "List cargo crates");
        var packages = CargoParsing.ParseInstallList(r.StdOut).Select(c => new ToolPackage(c.Name, c.Version, c.Version,
            Pinned: c.Source is not null, Source: "crates.io",
            Note: c.Source is null ? null : $"Installed from {c.Source}; UpdateForge will not replace it.")).ToList();
        return checkUpdates ? await WithLatestAsync(packages, p => LatestAsync(p, cancellationToken), cancellationToken).ConfigureAwait(false) : packages;
    }

    private async Task<string?> LatestAsync(ToolPackage p, CancellationToken ct)
    {
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "search", p.Name, "--limit", "10"), ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0) return null;
        var hit = CargoParsing.ParseSearch(r.StdOut).FirstOrDefault(c => CargoParsing.Normalize(c.Name) == CargoParsing.Normalize(p.Name));
        return IsValidVersion(hit.Version) ? hit.Version : null;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "search", "--limit", "40", "--", query), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "Search crates.io");
        return [.. CargoParsing.ParseSearch(r.StdOut).Where(c => IsValidCrate(c.Name)).Select(c => new PackageSearchResult(ProviderKey, c.Name, c.Name, c.Version, "crates.io"))];
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidCrate(package.PackageId)) throw new ArgumentException("Crate name failed validation.", nameof(package));
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "info", package.PackageId), cancellationToken).ConfigureAwait(false);
        return CargoParsing.ParseInfo(PackageManagerOutput.RequireSuccess(r, "Read crate details"));
    }
}
