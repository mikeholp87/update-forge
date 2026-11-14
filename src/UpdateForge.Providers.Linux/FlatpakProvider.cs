using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Linux;

public sealed record FlatpakApp(string Id, string Name, string Version, string Branch, string Origin, bool System, string Arch);

public sealed record FlatpakUpdate(string Id, string Version, string Branch, string Origin);

public static class FlatpakParsing
{
    /// <summary>Parses <c>flatpak list --app --columns=application,name,version,branch,origin,installation,arch</c> (tab separated).</summary>
    public static IReadOnlyList<FlatpakApp> ParseList(string output)
    {
        var list = new List<FlatpakApp>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length < 7 || !FlatpakProvider.IsValidId(f[0])) continue;
            list.Add(new FlatpakApp(f[0], f[1], f[2], f[3], f[4], !f[5].Equals("user", StringComparison.OrdinalIgnoreCase), f[6]));
        }
        return list;
    }

    /// <summary>Parses <c>flatpak remote-ls --updates --app --columns=application,version,branch,origin</c>.</summary>
    public static IReadOnlyList<FlatpakUpdate> ParseUpdates(string output)
    {
        var list = new List<FlatpakUpdate>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length >= 4 && FlatpakProvider.IsValidId(f[0])) list.Add(new FlatpakUpdate(f[0], f[1], f[2], f[3]));
        }
        return list;
    }

    /// <summary>Parses <c>flatpak search --columns=application,name,version,branch,remotes</c>.</summary>
    public static IReadOnlyList<PackageSearchResult> ParseSearch(string output)
    {
        var list = new List<PackageSearchResult>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length < 5 || !FlatpakProvider.IsValidId(f[0])) continue;
            var remote = f[4].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(FlatpakProvider.IsValidRemote);
            list.Add(new PackageSearchResult(FlatpakProvider.ProviderKey, f[0], f[1].Length == 0 ? f[0] : f[1], f[2].Length == 0 ? "Unknown" : f[2], remote));
        }
        return list;
    }
}

/// <summary>
/// Flatpak applications (runtimes are not listed). Updates install the application ID into the installation it came from
/// (<c>--user</c> or <c>--system</c>); flatpak asks polkit for system-wide changes itself, so UpdateForge never runs it as root.
/// Unattended runs leave system-wide updates pending because the authorization dialog needs a person.
/// </summary>
public sealed partial class FlatpakProvider : IUpdateProvider, IPackageManager, IPackageSearchProvider
{
    public const string ProviderKey = "flatpak";
    private readonly IProcessRunner _runner;
    private readonly ILogger<FlatpakProvider> _log;
    private readonly string? _binDirectory;
    private readonly Func<bool> _isRoot;

    public FlatpakProvider(IProcessRunner runner, ILogger<FlatpakProvider> log, string? binDirectory = null, Func<bool>? isRoot = null)
    {
        _runner = runner;
        _log = log;
        _binDirectory = binDirectory;
        _isRoot = isRoot ?? (() => Environment.IsPrivilegedProcess);
    }

    public string Id => ProviderKey;
    public string DisplayName => "Flatpak";
    public string ExecutablePath => Flatpak ?? "flatpak";
    public bool CanRefreshSources => true;
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: true, CanUpdate: true, SupportsPins: false);

    private string? Flatpak => LinuxTools.Find("flatpak", _binDirectory);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*(\.[A-Za-z_][A-Za-z0-9_\-]*){1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPattern();

    internal static bool IsValidId(string? id) => id is { Length: <= 255 } && IdPattern().IsMatch(id);

    internal static bool IsValidRemote(string? remote) => remote is not null && RemotePattern().IsMatch(remote);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) =>
        (await LinuxTools.ProbeAsync(_runner, Flatpak, "Flatpak is not installed.", "--version", cancellationToken).ConfigureAwait(false)).ToAvailability();

    private ProcessSpec Spec(TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(Flatpak ?? throw new InvalidOperationException("Flatpak is not installed."), args, timeout, onLine, LinuxTools.CLocale);

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "remotes", "--columns=name,url,options"), cancellationToken).ConfigureAwait(false), "List Flatpak remotes");

    public async Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await _runner.RunAsync(Spec(TimeSpan.FromMinutes(5), null, "update", "--appstream", "-y", "--noninteractive"), cancellationToken).ConfigureAwait(false), "Refresh Flatpak appstream data");

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "list", "--app", "--columns=application,name,version,branch,origin,installation,arch"), cancellationToken).ConfigureAwait(false);
        var installed = FlatpakParsing.ParseList(PackageManagerOutput.RequireSuccess(list, "List Flatpak applications"));

        // Pending updates are asked per installation so each one is applied to the installation it belongs to.
        var updates = new Dictionary<(string Id, bool System), FlatpakUpdate>();
        foreach (var system in new[] { false, true })
        {
            var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(3), null, "remote-ls", "--updates", "--app", system ? "--system" : "--user",
                "--columns=application,version,branch,origin"), cancellationToken).ConfigureAwait(false);
            if (r.TimedOut || r.ExitCode != 0) throw new InvalidOperationException($"flatpak remote-ls failed (exit code {r.ExitCode}). {r.StdErr.Trim()}");
            foreach (var u in FlatpakParsing.ParseUpdates(r.StdOut)) updates[(u.Id, system)] = u;
        }

        var now = DateTimeOffset.UtcNow;
        var root = _isRoot();
        return [.. installed.Select(a =>
        {
            updates.TryGetValue((a.Id, a.System), out var u);
            var hasUpdate = u is not null;
            // Flatpak versions are the app's own strings and sometimes absent; an update is real because flatpak says so.
            var display = hasUpdate ? (u!.Version.Length > 0 ? u.Version : "newer build") : null;
            return new InstalledApplication
            {
                Id = $"{ProviderKey}:{(a.System ? "system" : "user")}:{a.Id}:{a.Branch}",
                Name = a.Name.Length > 0 ? a.Name : a.Id,
                ProviderId = ProviderKey,
                PackageId = a.Id,
                PackageSource = a.Origin,
                InstalledVersion = a.Version.Length > 0 ? a.Version : a.Branch,
                AvailableVersion = display,
                Architecture = a.Arch,
                Scope = a.System ? InstallScope.Machine : InstallScope.User,
                Status = hasUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
                Support = UpdateSupport.AutoUpdatable,
                RequiresElevation = hasUpdate && a.System && !root,
                Note = a.Branch.Length > 0 ? $"Branch {a.Branch}" : null,
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, a.Id, a.Origin)],
            };
        })];
    }

    // Flatpak versions are free-form app strings ("2024.1-beta", "r123", a branch name when absent). When they cannot be ordered but
    // changed after a successful update, treat that as progress.
    // ponytail: a rebuild that keeps the same version string is reported as "did not change"; compare the commit column if that shows up.
    public VersionOrder CompareVersions(string? installed, string? candidate)
    {
        var order = AppVersion.Compare(installed, candidate);
        if (order != VersionOrder.Unknown) return order;
        return string.Equals(installed, candidate, StringComparison.Ordinal) ? VersionOrder.Equal : VersionOrder.Older;
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        if (app.ProviderId != ProviderKey || !IsValidId(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Flatpak application ID failed validation; refusing to run.");
        if (app.Scope == InstallScope.Unknown)
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Flatpak installation (user or system) is unknown; refusing to run.");
        _log.LogInformation("Updating Flatpak {Package} ({Scope})", app.PackageId, app.Scope);
        return await RunAsync(["update", "-y", "--noninteractive", "--app", app.Scope == InstallScope.User ? "--user" : "--system", app.PackageId!],
            "Updated with Flatpak.", progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsValidId(request.PackageId) || !IsValidRemote(request.Source))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Flatpak application ID or remote failed validation; refusing to run.");
        if (request.Version is { } v && !BranchPattern().IsMatch(v))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Flatpak branch failed validation; refusing to run.");
        _log.LogInformation("Installing Flatpak {Package} (correlation {Correlation})", request.PackageId, request.CorrelationId);
        return await RunAsync(["install", "-y", "--noninteractive", request.Source!, request.Version is null ? request.PackageId : $"{request.PackageId}//{request.Version}"],
            "Installed.", progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateResult> RunAsync(string[] args, string successMessage, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new UpdateProgress("Starting Flatpak…", null, "Installing"));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(30), l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }, args), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "Flatpak timed out.");

        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        var tail = LinuxTools.Tail(text);
        if (r.ExitCode == 0) return new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = successMessage, TechnicalDetails = tail };

        if (text.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || text.Contains("not allowed", StringComparison.OrdinalIgnoreCase) || text.Contains("polkit", StringComparison.OrdinalIgnoreCase))
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "Flatpak needs authorization for a system-wide change. Approve the prompt, or run the update from a terminal." };
        if (text.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) || text.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) || text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return UpdateResult.Fail(ErrorCategory.Network, "Flatpak could not reach its remote.", r.ExitCode, tail);
        if (text.Contains("Not enough disk space", StringComparison.OrdinalIgnoreCase))
            return UpdateResult.Fail(ErrorCategory.DiskSpace, "Not enough disk space.", r.ExitCode, tail);
        return UpdateResult.Fail(ErrorCategory.InstallerFailed, $"Flatpak exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (!IsValidId(application.PackageId)) return null;
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "list", "--app", "--columns=application,name,version,branch,origin,installation,arch"), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0) return null;
        var system = application.Scope != InstallScope.User;
        var a = FlatpakParsing.ParseList(r.StdOut).FirstOrDefault(x => x.Id == application.PackageId && x.System == system);
        return a is null ? null : a.Version.Length > 0 ? a.Version : a.Branch;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "search", "--columns=application,name,version,branch,remotes", query), cancellationToken).ConfigureAwait(false);
        // flatpak search exits 1 with "No matches found" on stderr; that is an empty result, not a failure.
        if (r.ExitCode == 1 && r.StdOut.Trim().Length == 0) return [];
        return FlatpakParsing.ParseSearch(PackageManagerOutput.RequireSuccess(r, "Search Flatpak applications"));
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidId(package.PackageId) || !IsValidRemote(package.Source)) throw new ArgumentException("Flatpak application ID or remote failed validation.", nameof(package));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "remote-info", package.Source!, package.PackageId), cancellationToken).ConfigureAwait(false);
        return PackageDetails.From(PackageDetailsText.Parse(PackageManagerOutput.RequireSuccess(r, "Read Flatpak details")));
    }
}
