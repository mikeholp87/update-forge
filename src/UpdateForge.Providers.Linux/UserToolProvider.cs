using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Linux;

/// <summary>
/// One globally installed package as a per-user tool reports it. <paramref name="Latest"/> is the newest version the tool knows of
/// (equal to <paramref name="Version"/> when current); null means the check could not be made.
/// </summary>
internal sealed record ToolPackage(string Name, string Version, string? Latest = null, bool Pinned = false, string? Note = null, string? Source = null, string? Key = null);

/// <summary>
/// Shared behavior of the per-user package managers (cargo, pnpm, bun, yarn, pipx, Homebrew): tool lookup, inventory mapping,
/// argument validation and install/update execution. Subclasses supply what differs: how to list packages, how to look up
/// the newest version, and the exact command line. Nothing here uses a shell, every package name is validated before it reaches
/// an argument list, and these tools never run as root.
/// </summary>
public abstract partial class UserToolProvider : IUpdateProvider, IPackageManager
{
    private readonly string? _binDirectory;

    private protected UserToolProvider(IProcessRunner runner, ILogger log, string? binDirectory)
    {
        Runner = runner;
        Log = log;
        _binDirectory = binDirectory;
    }

    internal IProcessRunner Runner { get; }
    internal ILogger Log { get; }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    internal abstract string ExeName { get; }

    public string ExecutablePath => Exe ?? ExeName;
    public virtual bool CanRefreshSources => false;
    internal virtual bool SupportsPins => false;
    internal virtual bool SupportsVersionPin => true;
    internal virtual int InstallTimeoutMinutes => 30;
    internal virtual string VersionArg => "--version";
    internal virtual IReadOnlyDictionary<string, string> Env => LinuxTools.CLocale;

    public ProviderCapabilities Capabilities => new(CanDiscover: true, CanDetectUpdates: true, CanUpdate: true, SupportsPins: SupportsPins);

    internal string? Exe => LinuxTools.FindUser(ExeName, _binDirectory);

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    internal static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    internal abstract bool IsValidName(string? name);

    /// <summary>Installed packages; with <paramref name="checkUpdates"/> also the newest version of each. Throws when the tool itself fails.</summary>
    internal abstract Task<IReadOnlyList<ToolPackage>> ListAsync(bool checkUpdates, CancellationToken cancellationToken);

    /// <summary>The command line for an install or update, or null when an argument fails validation.</summary>
    internal abstract string[]? InstallArgs(string name, string? version, string? source, bool update);

    /// <summary>A problem that makes the installed tool unusable (for example an unsupported major version), or null.</summary>
    internal virtual string? VersionProblem(string versionLine) => null;

    internal virtual string? Hint(string output) => null;

    internal virtual string[]? SourcesArgs => null;
    internal virtual string SourcesFallback => DisplayName + " uses its default catalog.";

    internal ProcessSpec Spec(TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(Exe ?? throw new InvalidOperationException(DisplayName + " is not installed."), args, timeout, onLine, Env);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        var probe = await LinuxTools.ProbeAsync(Runner, Exe, DisplayName + " is not installed.", VersionArg, cancellationToken, Env).ConfigureAwait(false);
        if (probe.Problem is null && VersionProblem(probe.Version ?? "") is { } problem) return ProviderAvailability.Unavailable(problem);
        return probe.ToAvailability();
    }

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken) =>
        SourcesArgs is null ? SourcesFallback
        : PackageManagerOutput.RequireSuccess(await Runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, SourcesArgs), cancellationToken).ConfigureAwait(false), "Read " + DisplayName + " sources");

    public virtual Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(DisplayName + " queries its catalog on every check; there is nothing to refresh.");

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var packages = await ListAsync(true, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return [.. packages.Select(p =>
        {
            var newer = !p.Pinned && IsNewer(p.Version, p.Latest);
            return new InstalledApplication
            {
                Id = $"{Id}:{p.Key ?? p.Name}",
                Name = p.Name,
                ProviderId = Id,
                PackageId = p.Name,
                PackageSource = p.Source ?? Id,
                InstalledVersion = p.Version,
                AvailableVersion = newer ? p.Latest : null,
                Scope = InstallScope.User,
                Status = newer ? UpdateStatus.UpdateAvailable : p.Latest is null ? UpdateStatus.Unknown : UpdateStatus.UpToDate,
                Support = UpdateSupport.AutoUpdatable,
                IsPinned = p.Pinned,
                Note = p.Note,
                LastChecked = now,
                Evidence = [new SourceEvidence(Id, p.Key ?? p.Name, p.Source ?? Id)],
            };
        })];
    }

    /// <summary>Unparsable versions (Homebrew's <c>1.2_1</c>, date stamps) fall back to "different means newer": the tool named that version as latest.</summary>
    private static bool IsNewer(string installed, string? latest) =>
        latest is not null && latest != installed
        && AppVersion.Compare(installed, latest) is VersionOrder.Older or VersionOrder.Unknown;

    /// <summary>
    /// Fills <see cref="ToolPackage.Latest"/> by looking each unpinned package up individually, four at a time. A single failed lookup
    /// leaves that package's status unknown; if every lookup fails the check itself failed (offline, registry down) and that must
    /// not be reported as "everything is up to date".
    /// </summary>
    internal static async Task<IReadOnlyList<ToolPackage>> WithLatestAsync(IReadOnlyList<ToolPackage> packages, Func<ToolPackage, Task<string?>> lookup, CancellationToken ct)
    {
        var targets = packages.Where(p => !p.Pinned).ToList();
        var latest = await LinuxTools.SelectLimitedAsync(targets, 4, lookup, ct).ConfigureAwait(false);
        if (targets.Count > 0 && latest.All(l => l is null))
            throw new InvalidOperationException("Could not check for updates; the registry did not answer. Are you offline?");
        var map = targets.Zip(latest).ToDictionary(t => t.First.Name, t => t.Second, StringComparer.Ordinal);
        return [.. packages.Select(p => map.TryGetValue(p.Name, out var l) ? p with { Latest = l } : p)];
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        var version = SupportsVersionPin ? request.TargetVersion ?? app.AvailableVersion : null;
        if (app.ProviderId != Id || !IsValidName(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " package name failed validation; refusing to run.");
        if (SupportsVersionPin && !IsValidVersion(version))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " target version failed validation; refusing to run.");
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = app.Note ?? "Package is pinned." };
        var args = InstallArgs(app.PackageId!, version, app.PackageSource, update: true);
        if (args is null) return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " package details failed validation; refusing to run.");

        Log.LogInformation("Updating {Provider} package {Package} to {Version}", Id, app.PackageId, version ?? "latest");
        return await RunAsync(args, "Updated with " + DisplayName + ".", progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsValidName(request.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " package name failed validation; refusing to run.");
        if (request.Version is not null && !SupportsVersionPin)
            return UpdateResult.Fail(ErrorCategory.NoApplicableInstaller, DisplayName + " cannot install a specific version.");
        if (request.Version is not null && !IsValidVersion(request.Version))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " version failed validation; refusing to run.");
        var args = InstallArgs(request.PackageId, request.Version, request.Source, update: false);
        if (args is null) return UpdateResult.Fail(ErrorCategory.SecurityBlocked, DisplayName + " package details failed validation; refusing to run.");

        Log.LogInformation("Installing {Provider} package {Package} (correlation {Correlation})", Id, request.PackageId, request.CorrelationId);
        return await RunAsync(args, "Installed.", progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateResult> RunAsync(string[] args, string successMessage, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new UpdateProgress($"Starting {DisplayName}…", null, "Installing"));
        var r = await Runner.RunAsync(Spec(TimeSpan.FromMinutes(InstallTimeoutMinutes),
            l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }, args), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, DisplayName + " timed out.");

        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        var tail = LinuxTools.Tail(text);
        if (r.ExitCode == 0) return new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = successMessage, TechnicalDetails = tail };

        if (Has(text, "EACCES", "EPERM", "permission denied"))
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = DisplayName + " could not write to its install folder (permission denied). Fix the folder's ownership; do not run UpdateForge as root." };
        if (Has(text, "ETXTBSY", "Text file busy", "EBUSY"))
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Error = ErrorCategory.ApplicationInUse, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "A file is in use. Close the program that is using this package and try again." };
        if (Has(text, "No space left on device"))
            return UpdateResult.Fail(ErrorCategory.DiskSpace, "The disk is full.", r.ExitCode, tail);
        if (Has(text, "ENOTFOUND", "ETIMEDOUT", "ECONNRESET", "Temporary failure in name resolution", "network is unreachable",
                "spurious network error", "error sending request", "Could not resolve host", "Failed to connect"))
            return UpdateResult.Fail(ErrorCategory.Network, DisplayName + " could not reach its registry.", r.ExitCode, tail);
        return UpdateResult.Fail(ErrorCategory.InstallerFailed, Hint(text) ?? $"{DisplayName} exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    private static bool Has(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (!IsValidName(application.PackageId)) return null;
        try
        {
            return (await ListAsync(false, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => p.Name.Equals(application.PackageId, StringComparison.OrdinalIgnoreCase) && (p.Source is null || p.Source == application.PackageSource))?.Version;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException) { return null; }
    }
}
