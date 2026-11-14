using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Linux;

public sealed record AptPackage(string Name, string Version, string Architecture, string Summary);

public sealed record AptUpgrade(string Name, string Architecture, string Suite, string Candidate, string Installed);

public static partial class AptParsing
{
    /// <summary>
    /// Parses <c>dpkg-query -W -f='${Package}\t${Version}\t${Architecture}\t${db:Status-Abbrev}\t${binary:Summary}\n'</c>.
    /// Only fully installed packages (status <c>ii</c>) are returned.
    /// </summary>
    public static IReadOnlyList<AptPackage> ParseInstalled(string output)
    {
        var list = new List<AptPackage>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length < 4 || !f[3].StartsWith("ii", StringComparison.Ordinal)) continue;
            if (f[0].Length == 0 || f[1].Length == 0) continue;
            list.Add(new AptPackage(f[0], f[1], f[2], f.Length > 4 ? f[4] : ""));
        }
        return list;
    }

    /// <summary>Parses <c>apt list --upgradable</c> (run with the C locale): <c>name/suite candidate arch [upgradable from: installed]</c>.</summary>
    public static IReadOnlyList<AptUpgrade> ParseUpgradable(string output)
    {
        var list = new List<AptUpgrade>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var m = UpgradePattern().Match(line);
            if (m.Success) list.Add(new AptUpgrade(m.Groups["n"].Value, m.Groups["a"].Value, m.Groups["s"].Value, m.Groups["c"].Value, m.Groups["f"].Value));
        }
        return list;
    }

    /// <summary>Package names (optionally <c>name:arch</c>), one per line, as printed by <c>apt-mark showmanual</c> / <c>showhold</c>.</summary>
    public static IReadOnlySet<string> ParseNames(string output) =>
        LinuxTools.Lines(output).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Parses <c>apt-cache search --names-only</c> (<c>name - description</c>); names that are not valid Debian package names are dropped.</summary>
    public static IReadOnlyList<(string Name, string Description)> ParseSearch(string output)
    {
        var list = new List<(string, string)>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var i = line.IndexOf(" - ", StringComparison.Ordinal);
            var name = (i < 0 ? line : line[..i]).Trim();
            if (AptProvider.IsValidName(name)) list.Add((name, i < 0 ? "" : line[(i + 3)..].Trim()));
        }
        return list;
    }

    /// <summary>Candidate versions from <c>apt-cache policy a b c</c>, keyed by package name.</summary>
    public static IReadOnlyDictionary<string, string> ParseCandidates(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && raw.EndsWith(':')) current = raw[..^1];
            else if (current is not null && raw.TrimStart().StartsWith("Candidate:", StringComparison.Ordinal))
            {
                var v = raw.TrimStart()["Candidate:".Length..].Trim();
                if (v.Length > 0 && v != "(none)") map[current] = v;
            }
        }
        return map;
    }

    /// <summary>Package sources from <c>apt-cache policy</c>: the <c>500 http://… suite/component</c> lines, without duplicates.</summary>
    public static IReadOnlyList<string> ParseSources(string policy) =>
        [.. LinuxTools.Lines(policy).Select(l => SourceLine().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()];

    [GeneratedRegex(@"^(?<n>[^/\s]+)/(?<s>\S+) (?<c>\S+) (?<a>\S+) \[upgradable from: (?<f>[^\]\s]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex UpgradePattern();

    [GeneratedRegex(@"^\s*(?:\*\*\* )?\d+ (\S+ \S+ \S+)", RegexOptions.CultureInvariant)]
    private static partial Regex SourceLine();
}

/// <summary>
/// apt (Debian, Ubuntu and derivatives). The inventory is the packages you installed on purpose (<c>apt-mark showmanual</c>) plus
/// anything with a pending upgrade, so the list is not flooded with hundreds of libraries. Upgrades install the exact version that was
/// checked. Installing needs root: when UpdateForge is not root the command runs through <c>pkexec</c> (polkit shows the
/// password prompt), and unattended runs leave such updates pending. Held packages are reported as pinned and never touched.
/// </summary>
public sealed partial class AptProvider : IUpdateProvider, IPackageManager, IPackageSearchProvider
{
    public const string ProviderKey = "apt";
    private readonly IProcessRunner _runner;
    private readonly ILogger<AptProvider> _log;
    private readonly string? _binDirectory;
    private readonly Func<bool> _isRoot;
    private readonly Func<bool> _rebootRequired;

    public AptProvider(IProcessRunner runner, ILogger<AptProvider> log, string? binDirectory = null, Func<bool>? isRoot = null, Func<bool>? rebootRequired = null)
    {
        _runner = runner;
        _log = log;
        _binDirectory = binDirectory;
        _isRoot = isRoot ?? (() => Environment.IsPrivilegedProcess);
        _rebootRequired = rebootRequired ?? (() => File.Exists("/var/run/reboot-required"));
    }

    public string Id => ProviderKey;
    public string DisplayName => "apt";
    public string ExecutablePath => AptGet ?? "apt-get";
    public bool CanRefreshSources => true;
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: true, CanUpdate: true, SupportsPins: true);

    private string? AptGet => LinuxTools.Find("apt-get", _binDirectory);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9+.\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+~:\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchPattern();

    internal static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public VersionOrder CompareVersions(string? installed, string? candidate) => DebianVersion.Compare(installed, candidate);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (LinuxTools.Find("dpkg-query", _binDirectory) is null) return ProviderAvailability.NotInstalled("apt and dpkg are not installed (this is not a Debian-based system).");
        return (await LinuxTools.ProbeAsync(_runner, AptGet, "apt-get is not installed.", "--version", cancellationToken).ConfigureAwait(false)).ToAvailability();
    }

    private ProcessSpec Spec(string tool, TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(LinuxTools.Find(tool, _binDirectory) ?? throw new InvalidOperationException(tool + " is not installed."), args, timeout, onLine, LinuxTools.CLocale);

    /// <summary>Runs apt-get as root: directly when already root, otherwise through pkexec. Returns null when neither is possible.</summary>
    private ProcessSpec? Elevated(TimeSpan timeout, Action<string>? onLine, params string[] aptArgs)
    {
        var aptGet = AptGet;
        if (aptGet is null) return null;
        var env = new Dictionary<string, string>(LinuxTools.CLocale) { ["DEBIAN_FRONTEND"] = "noninteractive", ["APT_LISTCHANGES_FRONTEND"] = "none" };
        if (_isRoot()) return new ProcessSpec(aptGet, aptArgs, timeout, onLine, env);
        var pkexec = LinuxTools.Find("pkexec", _binDirectory);
        var envTool = LinuxTools.Find("env", _binDirectory);
        if (pkexec is null || envTool is null) return null;
        // pkexec starts the program with a clean environment, so the settings travel as arguments to env.
        return new ProcessSpec(pkexec, [envTool, .. env.Select(p => $"{p.Key}={p.Value}"), aptGet, .. aptArgs], timeout, onLine);
    }

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var policy = PackageManagerOutput.RequireSuccess(await _runner.RunAsync(Spec("apt-cache", TimeSpan.FromMinutes(1), null, "policy"), cancellationToken).ConfigureAwait(false), "List apt sources");
        var sources = AptParsing.ParseSources(policy);
        return sources.Count == 0 ? "No apt sources found." : string.Join('\n', sources);
    }

    public async Task<string> RefreshSourcesAsync(CancellationToken cancellationToken)
    {
        var spec = Elevated(TimeSpan.FromMinutes(5), null, "update") ?? throw new InvalidOperationException("Refreshing apt needs root; install polkit (pkexec) or run UpdateForge as root.");
        var r = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (r.ExitCode is 126 or 127 && r.StdOut.Length == 0) throw new InvalidOperationException("Authorization was declined.");
        return PackageManagerOutput.RequireSuccess(r, "Refresh apt sources");
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var installedTask = _runner.RunAsync(Spec("dpkg-query", TimeSpan.FromMinutes(2), null, "-W", "-f=${Package}\\t${Version}\\t${Architecture}\\t${db:Status-Abbrev}\\t${binary:Summary}\\n"), cancellationToken);
        var manualTask = _runner.RunAsync(Spec("apt-mark", TimeSpan.FromMinutes(1), null, "showmanual"), cancellationToken);
        var holdTask = _runner.RunAsync(Spec("apt-mark", TimeSpan.FromMinutes(1), null, "showhold"), cancellationToken);
        var upgradeTask = _runner.RunAsync(Spec("apt", TimeSpan.FromMinutes(2), null, "list", "--upgradable"), cancellationToken);
        await Task.WhenAll(installedTask, manualTask, holdTask, upgradeTask).ConfigureAwait(false);

        // A failed query must surface as a provider problem, never as "everything is up to date".
        var installed = AptParsing.ParseInstalled(PackageManagerOutput.RequireSuccess(installedTask.Result, "List installed packages"));
        var manual = AptParsing.ParseNames(PackageManagerOutput.RequireSuccess(manualTask.Result, "List manually installed packages"));
        var held = AptParsing.ParseNames(holdTask.Result.ExitCode == 0 ? holdTask.Result.StdOut : "");
        if (upgradeTask.Result.TimedOut || upgradeTask.Result.ExitCode != 0)
            throw new InvalidOperationException($"apt list failed (exit code {upgradeTask.Result.ExitCode}). {upgradeTask.Result.StdErr.Trim()}");
        var upgrades = AptParsing.ParseUpgradable(upgradeTask.Result.StdOut).GroupBy(u => (u.Name, u.Architecture)).ToDictionary(g => g.Key, g => g.First());

        var now = DateTimeOffset.UtcNow;
        var root = _isRoot();
        var apps = new List<InstalledApplication>();
        foreach (var p in installed)
        {
            upgrades.TryGetValue((p.Name, p.Architecture), out var up);
            if (up is null && !manual.Contains(p.Name) && !manual.Contains($"{p.Name}:{p.Architecture}")) continue;
            var isHeld = held.Contains(p.Name) || held.Contains($"{p.Name}:{p.Architecture}");
            var newer = up is not null && DebianVersion.Compare(p.Version, up.Candidate) == VersionOrder.Older;
            apps.Add(new InstalledApplication
            {
                Id = $"{ProviderKey}:{p.Name}:{p.Architecture}",
                Name = p.Name,
                ProviderId = ProviderKey,
                PackageId = p.Name,
                PackageSource = up?.Suite,
                InstalledVersion = p.Version,
                AvailableVersion = newer ? up!.Candidate : null,
                Architecture = p.Architecture,
                Scope = InstallScope.Machine,
                Status = newer ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
                Support = UpdateSupport.AutoUpdatable,
                IsPinned = isHeld,
                RequiresElevation = newer && !root,
                Note = isHeld ? "On hold (apt-mark hold); UpdateForge will not upgrade it." : p.Summary.Length > 0 ? p.Summary : null,
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, $"{p.Name}:{p.Architecture}", up?.Suite)],
            });
        }
        return apps;
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        var version = request.TargetVersion ?? app.AvailableVersion;
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Package is on hold (apt-mark hold)." };
        if (app.ProviderId != ProviderKey || !IsValidName(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "apt package name failed validation; refusing to run.");
        if (version is null || !VersionPattern().IsMatch(version))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "apt target version failed validation; refusing to run.");
        if (app.Architecture is { } arch && !ArchPattern().IsMatch(arch))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "apt architecture failed validation; refusing to run.");

        // Installing the exact version that was checked (not "latest") keeps the result identical to what the user was shown.
        var target = $"{app.PackageId}{(app.Architecture is null ? "" : ":" + app.Architecture)}={version}";
        _log.LogInformation("Upgrading apt package {Package}", target);
        return await RunInstallAsync(["install", "--only-upgrade", "-y", "-o", "Dpkg::Options::=--force-confold", target], "Upgraded with apt.", progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsValidName(request.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "apt package name failed validation; refusing to run.");
        if (request.Version is { } v && !VersionPattern().IsMatch(v))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "apt version failed validation; refusing to run.");
        _log.LogInformation("Installing apt package {Package} (correlation {Correlation})", request.PackageId, request.CorrelationId);
        return await RunInstallAsync(["install", "-y", "-o", "Dpkg::Options::=--force-confold", request.Version is null ? request.PackageId : $"{request.PackageId}={request.Version}"],
            "Installed.", progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateResult> RunInstallAsync(string[] aptArgs, string successMessage, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var spec = Elevated(TimeSpan.FromMinutes(30), l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }, aptArgs);
        if (spec is null)
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired,
                Message = "apt needs root. Install polkit (pkexec) or run: sudo apt-get " + string.Join(' ', aptArgs[..2]) + " …" };

        progress.Report(new UpdateProgress(_isRoot() ? "Starting apt…" : "Waiting for authorization…", null, "Installing"));
        var r = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "apt timed out.");

        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        var tail = LinuxTools.Tail(text);
        if (r.ExitCode == 0)
        {
            var restart = _rebootRequired();
            return new UpdateResult { Outcome = restart ? UpdateOutcome.RestartRequired : UpdateOutcome.Succeeded, ExitCode = 0, RestartRequired = restart,
                Message = restart ? successMessage + " Restart to finish." : successMessage, TechnicalDetails = tail };
        }

        // pkexec: 126 = the authorization dialog was dismissed, 127 = not authorized. Anything apt printed means apt itself ran.
        if (r.ExitCode == 126 && !text.Contains("apt", StringComparison.OrdinalIgnoreCase))
            return new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, ExitCode = r.ExitCode, Message = "Authorization was dismissed." };
        if (r.ExitCode == 127 && !text.Contains("E:", StringComparison.Ordinal))
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, ExitCode = r.ExitCode,
                TechnicalDetails = tail, Message = "Not authorized to run apt as root." };
        if (text.Contains("Could not get lock", StringComparison.Ordinal) || text.Contains("Unable to acquire the dpkg frontend lock", StringComparison.Ordinal))
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Error = ErrorCategory.InstallerRunning, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "Another package manager (apt, a software center or unattended-upgrades) is running. Try again when it finishes." };
        if (text.Contains("Temporary failure resolving", StringComparison.Ordinal) || text.Contains("Could not resolve", StringComparison.Ordinal) || text.Contains("Failed to fetch", StringComparison.Ordinal))
            return UpdateResult.Fail(ErrorCategory.Network, "apt could not download the package.", r.ExitCode, tail);
        if (text.Contains("was not found", StringComparison.Ordinal) || text.Contains("Unable to locate package", StringComparison.Ordinal))
            return UpdateResult.Fail(ErrorCategory.NoApplicableInstaller, "That version is no longer available. Refresh apt sources and scan again.", r.ExitCode, tail);
        return UpdateResult.Fail(ErrorCategory.InstallerFailed, $"apt exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (!IsValidName(application.PackageId)) return null;
        var target = application.PackageId + (application.Architecture is { } a && ArchPattern().IsMatch(a) ? ":" + a : "");
        var r = await _runner.RunAsync(Spec("dpkg-query", TimeSpan.FromMinutes(1), null, "-W", "-f=${Version}\\t${db:Status-Abbrev}\\n", target), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0) return null;
        var f = LinuxTools.Lines(r.StdOut).FirstOrDefault()?.Split('\t');
        return f is { Length: 2 } && f[1].StartsWith("ii", StringComparison.Ordinal) ? f[0] : null;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var r = await _runner.RunAsync(Spec("apt-cache", TimeSpan.FromMinutes(1), null, "search", "--names-only", query), cancellationToken).ConfigureAwait(false);
        var found = AptParsing.ParseSearch(PackageManagerOutput.RequireSuccess(r, "Search apt packages")).Take(40).ToList();
        if (found.Count == 0) return [];

        // One extra call fills in the candidate version of every hit.
        var policy = await _runner.RunAsync(Spec("apt-cache", TimeSpan.FromMinutes(1), null, ["policy", .. found.Select(f => f.Name)]), cancellationToken).ConfigureAwait(false);
        var versions = policy.ExitCode == 0 ? AptParsing.ParseCandidates(policy.StdOut) : new Dictionary<string, string>();
        return [.. found.Select(f => new PackageSearchResult(ProviderKey, f.Name, f.Name, versions.GetValueOrDefault(f.Name, "Unknown"), null))];
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidName(package.PackageId)) throw new ArgumentException("apt package name failed validation.", nameof(package));
        var r = await _runner.RunAsync(Spec("apt-cache", TimeSpan.FromMinutes(1), null, "show", package.PackageId), cancellationToken).ConfigureAwait(false);
        var text = PackageManagerOutput.RequireSuccess(r, "Read apt package details");
        // Several versions print as consecutive stanzas separated by an empty line; the first is the candidate.
        var stanza = text.Replace("\r", "", StringComparison.Ordinal).Split("\n\n")[0];
        return PackageDetails.From(PackageDetailsText.Parse(stanza, ["Description"]));
    }
}
