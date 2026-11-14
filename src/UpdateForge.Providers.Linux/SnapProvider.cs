using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Linux;

public sealed record SnapEntry(string Name, string Version, string Revision, string Tracking, string Publisher, string Notes);

public static class SnapParsing
{
    /// <summary>
    /// Parses <c>snap list</c> (<c>Name Version Rev Tracking Publisher Notes</c>). None of the columns contain spaces,
    /// so rows are split on whitespace. The header row and rows with invalid names are skipped.
    /// </summary>
    public static IReadOnlyList<SnapEntry> ParseList(string output)
    {
        var list = new List<SnapEntry>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5 || t[0] == "Name" || !SnapProvider.IsValidName(t[0])) continue;
            list.Add(new SnapEntry(t[0], t[1], t[2], t[3], t[4], t.Length > 5 ? t[5] : "-"));
        }
        return list;
    }

    /// <summary>
    /// Parses <c>snap refresh --list</c> (<c>Name Version Rev Size Publisher Notes</c>). Rows are anchored on names already
    /// known from <c>snap list</c>, so the header and the "All snaps up to date." message are never mistaken for data.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRefreshList(string output, IReadOnlySet<string> known)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in LinuxTools.Lines(output))
        {
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 3 && known.Contains(t[0])) map[t[0]] = t[1];
        }
        return map;
    }

    /// <summary>Parses <c>snap find</c> (<c>Name Version Publisher Notes Summary</c>).</summary>
    public static IReadOnlyList<PackageSearchResult> ParseFind(string output)
    {
        var list = new List<PackageSearchResult>();
        foreach (var line in LinuxTools.Lines(output))
        {
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 3 || t[0] == "Name" || !SnapProvider.IsValidName(t[0])) continue;
            list.Add(new PackageSearchResult(SnapProvider.ProviderKey, t[0], t[0], t[1], "snapcraft"));
        }
        return list;
    }
}

/// <summary>
/// Snap packages. Refreshes go through snapd, which asks polkit for authorization itself, so UpdateForge never runs
/// snap as root. Unattended runs leave refreshes pending because the authorization dialog needs a person (snapd also refreshes
/// on its own schedule). Base snaps (<c>core22</c>, …) are not listed; held snaps are reported as pinned.
/// </summary>
public sealed partial class SnapProvider : IUpdateProvider, IPackageManager, IPackageSearchProvider
{
    public const string ProviderKey = "snap";
    private readonly IProcessRunner _runner;
    private readonly ILogger<SnapProvider> _log;
    private readonly string? _binDirectory;
    private readonly Func<bool> _isRoot;

    public SnapProvider(IProcessRunner runner, ILogger<SnapProvider> log, string? binDirectory = null, Func<bool>? isRoot = null)
    {
        _runner = runner;
        _log = log;
        _binDirectory = binDirectory;
        _isRoot = isRoot ?? (() => Environment.IsPrivilegedProcess);
    }

    public string Id => ProviderKey;
    public string DisplayName => "Snap";
    public string ExecutablePath => Snap ?? "snap";
    public bool CanRefreshSources => false;
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: true, CanUpdate: true, SupportsPins: true);

    private string? Snap => LinuxTools.Find("snap", _binDirectory);

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9\-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        // `snap version` prints "snap  2.x" on the first line; `--version` is accepted too but only `version` is documented.
        var probe = await LinuxTools.ProbeAsync(_runner, Snap, "Snap is not installed.", "version", cancellationToken).ConfigureAwait(false);
        return probe.ToAvailability();
    }

    private ProcessSpec Spec(TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(Snap ?? throw new InvalidOperationException("Snap is not installed."), args, timeout, onLine, LinuxTools.CLocale);

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "refresh", "--time"), cancellationToken).ConfigureAwait(false), "Read snap refresh schedule");

    public Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Snap checks the store on every check; there is nothing to refresh.");

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "list"), cancellationToken).ConfigureAwait(false);
        var installed = SnapParsing.ParseList(PackageManagerOutput.RequireSuccess(list, "List snaps"));

        // `snap refresh --list` exits 0 and writes "All snaps up to date." to stderr when nothing is pending.
        var refresh = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "refresh", "--list"), cancellationToken).ConfigureAwait(false);
        if (refresh.TimedOut || refresh.ExitCode != 0) throw new InvalidOperationException($"snap refresh --list failed (exit code {refresh.ExitCode}). {refresh.StdErr.Trim()}");
        var pending = SnapParsing.ParseRefreshList(refresh.StdOut, installed.Select(s => s.Name).ToHashSet(StringComparer.Ordinal));

        var now = DateTimeOffset.UtcNow;
        var root = _isRoot();
        return [.. installed.Where(s => !s.Notes.Split(',').Contains("base")).Select(s =>
        {
            var held = s.Notes.Split(',').Contains("held");
            pending.TryGetValue(s.Name, out var next);
            var hasUpdate = next is not null;
            return new InstalledApplication
            {
                Id = $"{ProviderKey}:{s.Name}",
                Name = s.Name,
                ProviderId = ProviderKey,
                PackageId = s.Name,
                PackageSource = s.Tracking,
                Publisher = s.Publisher == "-" ? null : s.Publisher.TrimEnd('*'),
                InstalledVersion = s.Version,
                AvailableVersion = next,
                Scope = InstallScope.Machine,
                Status = hasUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
                Support = UpdateSupport.AutoUpdatable,
                IsPinned = held,
                RequiresElevation = hasUpdate && !root,
                Note = held ? "Refreshes are held (snap refresh --hold); UpdateForge will not refresh it." : $"Channel {s.Tracking}, revision {s.Revision}",
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, s.Name, s.Tracking)],
            };
        })];
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        if (app.ProviderId != ProviderKey || !IsValidName(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Snap name failed validation; refusing to run.");
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Snap refreshes are held." };
        _log.LogInformation("Refreshing snap {Package}", app.PackageId);
        return await RunAsync(["refresh", app.PackageId!], "Refreshed with snap.", progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsValidName(request.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Snap name failed validation; refusing to run.");
        _log.LogInformation("Installing snap {Package} (correlation {Correlation})", request.PackageId, request.CorrelationId);
        return await RunAsync(["install", request.PackageId], "Installed.", progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateResult> RunAsync(string[] args, string successMessage, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new UpdateProgress("Starting snap…", null, "Installing"));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(30), l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }, args), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "snap timed out.");

        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        var tail = LinuxTools.Tail(text);
        if (r.ExitCode == 0) return new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = successMessage, TechnicalDetails = tail };

        if (text.Contains("access denied", StringComparison.OrdinalIgnoreCase) || text.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "snapd needs authorization. Approve the prompt, or run: sudo snap " + args[0] + " …" };
        if (text.Contains("has running apps", StringComparison.OrdinalIgnoreCase) || text.Contains("running apps", StringComparison.OrdinalIgnoreCase))
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Error = ErrorCategory.ApplicationInUse, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "The snap is running. Close it and try again." };
        if (text.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) || text.Contains("no such host", StringComparison.OrdinalIgnoreCase) || text.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase))
            return UpdateResult.Fail(ErrorCategory.Network, "snap could not reach the store.", r.ExitCode, tail);
        return UpdateResult.Fail(ErrorCategory.InstallerFailed, $"snap exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (!IsValidName(application.PackageId)) return null;
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "list", application.PackageId!), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0) return null;
        return SnapParsing.ParseList(r.StdOut).FirstOrDefault(s => s.Name == application.PackageId)?.Version;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "find", query), cancellationToken).ConfigureAwait(false);
        // "No matching snaps for ..." is an empty result, not a failure.
        if (r.StdOut.Trim().Length == 0 && r.StdErr.Contains("No matching", StringComparison.OrdinalIgnoreCase)) return [];
        return [.. SnapParsing.ParseFind(PackageManagerOutput.RequireSuccess(r, "Search snaps")).Take(40)];
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidName(package.PackageId)) throw new ArgumentException("Snap name failed validation.", nameof(package));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "info", package.PackageId), cancellationToken).ConfigureAwait(false);
        return PackageDetails.From(PackageDetailsText.Parse(PackageManagerOutput.RequireSuccess(r, "Read snap details")));
    }
}
