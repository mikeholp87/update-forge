using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.WinGet;

/// <summary>
/// WinGet provider backed by the WinGet CLI with a fixed executable path and typed arguments. Discovery is anchored
/// on the locale-independent <c>winget export</c> JSON. A COM (Microsoft.Management.Deployment) backend is a planned
/// alternative; see docs/RESEARCH.md.
/// </summary>
public sealed partial class WinGetProvider : IUpdateProvider, IPackageManager
{
    public const string ProviderKey = "winget";
    private static readonly string[] AllowedSources = ["winget", "msstore"];
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(3);
    // Parallel updates can collide on the single Windows Installer lock; wait and retry rather than failing the update.
    private const int MaxInstallerBusyAttempts = 5;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    private readonly IProcessRunner _runner;
    private readonly ILogger<WinGetProvider> _log;
    private readonly string _exe;
    private readonly Func<bool> _isElevated = static () => OperatingSystem.IsWindows()
        && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public WinGetProvider(IProcessRunner runner, ILogger<WinGetProvider> log, string? executablePath = null)
    {
        _runner = runner;
        _log = log;
        _exe = executablePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
    }

    public string Id => ProviderKey;
    public string ExecutablePath => _exe;
    public bool CanRefreshSources => true;

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await _runner.RunAsync(new ProcessSpec(_exe,
            ["source", "list", "--disable-interactivity"], TimeSpan.FromMinutes(1)), cancellationToken).ConfigureAwait(false), "List WinGet sources");

    public async Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        PackageManagerOutput.RequireSuccess(await _runner.RunAsync(new ProcessSpec(_exe,
            ["source", "update", "--disable-interactivity"], ScanTimeout), cancellationToken).ConfigureAwait(false), "Refresh WinGet sources");
    public string DisplayName => "Windows Package Manager (WinGet)";
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: true, CanUpdate: true, SupportsPins: true);

    // '+' is legal in real WinGet identifiers (e.g. Notepad++.Notepad++). It is not a shell metacharacter, arguments are
    // passed as a typed list, and the first character must be alphanumeric so an ID can never look like an option.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private static readonly string[] CommonFlags = ["--accept-source-agreements", "--disable-interactivity"];

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_exe)) return ProviderAvailability.NotInstalled("WinGet (App Installer) is not installed.");
        try
        {
            var r = await _runner.RunAsync(new ProcessSpec(_exe, ["--version"], TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false);
            return r.ExitCode == 0 && !r.TimedOut
                ? new ProviderAvailability(ProviderHealth.Available, r.StdOut.Trim().TrimStart('v'), null)
                : ProviderAvailability.Unavailable($"WinGet returned exit code {r.ExitCode}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ProviderAvailability.Unavailable("WinGet could not be started: " + ex.Message);
        }
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var exportFile = Path.Combine(Path.GetTempPath(), $"updateforge-export-{Guid.NewGuid():N}.json");
        try
        {
            // Exit code may be non-zero when some installed software is not in any source; the file is still written.
            await _runner.RunAsync(new ProcessSpec(_exe, ["export", "-o", exportFile, "--include-versions", .. CommonFlags], ScanTimeout), cancellationToken).ConfigureAwait(false);
            if (!File.Exists(exportFile)) throw new InvalidOperationException("WinGet did not produce an export file.");
            var exported = WinGetParsing.ParseExport(await File.ReadAllTextAsync(exportFile, cancellationToken).ConfigureAwait(false));

            var ids = new HashSet<string>(exported.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var sources = new HashSet<string>(exported.Select(e => e.Source).Where(s => s.Length > 0).Append("winget").Append("msstore"), StringComparer.OrdinalIgnoreCase);

            var listTask = RunTable(["list", .. CommonFlags], cancellationToken);
            var upgradeTask = RunTable(["upgrade", "--include-unknown", "--include-pinned", .. CommonFlags], cancellationToken);
            var pinTask = RunTable(["pin", "list", .. CommonFlags], cancellationToken);
            await Task.WhenAll(listTask, upgradeTask, pinTask).ConfigureAwait(false);

            var names = WinGetParsing.ParseRows(listTask.Result, ids, sources).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var upgrades = WinGetParsing.ParseRows(upgradeTask.Result, ids, sources).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var pinned = WinGetParsing.ParseRows(pinTask.Result, ids, sources).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var now = DateTimeOffset.UtcNow;
            var apps = new List<InstalledApplication>();
            foreach (var e in exported)
            {
                upgrades.TryGetValue(e.Id, out var up);
                var installedKnown = AppVersion.TryParse(e.Version, out _, out _);
                var isStore = string.Equals(e.Source, "msstore", StringComparison.OrdinalIgnoreCase);

                UpdateStatus status;
                string? note = null;
                var support = isStore ? UpdateSupport.ApprovalRequired : UpdateSupport.AutoUpdatable;
                if (up is not null && installedKnown) status = UpdateStatus.UpdateAvailable;
                else if (up is not null)
                {
                    status = UpdateStatus.Unknown;
                    support = UpdateSupport.ManualRequired;
                    note = "WinGet does not know the installed version, so it cannot be compared safely.";
                }
                else status = installedKnown ? UpdateStatus.UpToDate : UpdateStatus.Unknown;
                if (isStore) note = "Microsoft Store package; update through the Store.";

                apps.Add(new InstalledApplication
                {
                    Id = $"{ProviderKey}:{e.Id}",
                    Name = names.TryGetValue(e.Id, out var n) ? n : e.Id,
                    ProviderId = ProviderKey,
                    PackageId = e.Id,
                    PackageSource = e.Source,
                    InstalledVersion = e.Version,
                    AvailableVersion = up?.Available,
                    Status = status,
                    Support = support,
                    IsPinned = pinned.Contains(e.Id),
                    Note = note,
                    LastChecked = now,
                    Evidence = [new SourceEvidence(ProviderKey, e.Id, e.Source)],
                });
            }
            return apps;
        }
        finally
        {
            try { File.Delete(exportFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> RunTable(IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await _runner.RunAsync(new ProcessSpec(_exe, args, ScanTimeout), ct).ConfigureAwait(false);
        if (r.TimedOut) throw new TimeoutException($"winget {args[0]} timed out.");
        return r.StdOut;
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        var validation = Validate(request);
        if (validation is not null) return validation;

        var args = new List<string>
        {
            "upgrade", "--id", app.PackageId!, "--exact", "--source", app.PackageSource!,
            "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (request.TargetVersion is { Length: > 0 } tv) { args.Add("--version"); args.Add(tv); }

        _log.LogInformation("Updating {PackageId} via WinGet (correlation {Correlation})", app.PackageId, request.CorrelationId);
        progress.Report(new UpdateProgress("Starting WinGet…", null, "Installing"));
        try
        {
            UpdateResult result = default!;
            for (var attempt = 1; attempt <= MaxInstallerBusyAttempts; attempt++)
            {
                var r = await _runner.RunAsync(new ProcessSpec(_exe, args, InstallTimeout, line => ReportLine(line, progress)), cancellationToken).ConfigureAwait(false);
                if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "The installer timed out and was stopped.");
                var details = Tail(r.StdOut);
                result = WinGetExitCodes.Map(r.ExitCode, details, _isElevated());
                // Another installer (often the Windows Installer service or a Store update) holds the lock: wait once and retry.
                if (attempt < MaxInstallerBusyAttempts && result.Error == ErrorCategory.InstallerRunning)
                {
                    progress.Report(new UpdateProgress("Another installation is in progress; waiting to retry ("+attempt+")…", null, "Waiting"));
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (result.Error == ErrorCategory.ApplicationInUse && LockingProcesses(details) is { Length: > 0 } names)
                    result = result with { Message = $"{names} is running and its files are locked. Close it and retry." };
                break;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, Message = "Canceled." };
        }
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        // The request may come from an imported bundle (untrusted), so it is validated exactly like an update request.
        if (!PackageIdPattern().IsMatch(request.PackageId ?? ""))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Package identifier failed validation; refusing to run.");
        if (!AllowedSources.Contains(request.Source ?? "", StringComparer.OrdinalIgnoreCase))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Package source is not an approved source; refusing to run.");
        if (request.Version is { } v && !VersionPattern().IsMatch(v))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Target version failed validation; refusing to run.");

        var args = new List<string>
        {
            "install", "--id", request.PackageId!, "--exact", "--source", request.Source!.ToLowerInvariant(),
            "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (request.Version is { Length: > 0 } tv) { args.Add("--version"); args.Add(tv); }

        _log.LogInformation("Installing {PackageId} via WinGet (correlation {Correlation})", request.PackageId, request.CorrelationId);
        progress.Report(new UpdateProgress("Starting WinGet…", null, "Installing"));
        try
        {
            var r = await _runner.RunAsync(new ProcessSpec(_exe, args, InstallTimeout, line => ReportLine(line, progress)), cancellationToken).ConfigureAwait(false);
            if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "The installer timed out and was stopped.");
            var result = WinGetExitCodes.Map(r.ExitCode, Tail(r.StdOut), _isElevated());
            return result.Outcome == UpdateOutcome.Succeeded ? result with { Message = "Installed." } : result;
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, Message = "Canceled." };
        }
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var probe = new UpdateRequest(application);
        if (Validate(probe) is { Outcome: not UpdateOutcome.Blocked }) return null;
        var r = await _runner.RunAsync(new ProcessSpec(_exe,
            ["list", "--id", application.PackageId!, "--exact", "--source", application.PackageSource!, .. CommonFlags], ScanTimeout), cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>([application.PackageId!], StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(["winget", "msstore"], StringComparer.OrdinalIgnoreCase);
        return WinGetParsing.ParseRows(r.StdOut, ids, sources) is { Count: > 0 } rows ? rows[0].Version : null;
    }

    /// <summary>Strict allowlist validation. Anything unexpected is refused, never sanitized into a command.</summary>
    internal static UpdateResult? Validate(UpdateRequest request)
    {
        var app = request.Application;
        if (app.ProviderId != ProviderKey || string.IsNullOrEmpty(app.PackageId) || !PackageIdPattern().IsMatch(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Package identifier failed validation; refusing to run.");
        if (app.PackageSource is null || !AllowedSources.Contains(app.PackageSource, StringComparer.OrdinalIgnoreCase))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Package source is not an approved source; refusing to run.");
        if (request.TargetVersion is { } v && !VersionPattern().IsMatch(v))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Target version failed validation; refusing to run.");
        if (app.IsPinned)
            return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Package is pinned." };
        return null;
    }

    private static void ReportLine(string line, IProgress<UpdateProgress> progress)
    {
        var t = line.Trim().Trim('\b', '\r', '-', '\\', '|', '/').Trim();
        if (t.Length < 4) return;
        var phase = t.Contains("Download", StringComparison.OrdinalIgnoreCase) ? "Downloading"
            : t.Contains("hash", StringComparison.OrdinalIgnoreCase) ? "Verifying" : "Installing";
        progress.Report(new UpdateProgress(t.Length > 200 ? t[..200] : t, null, phase));
    }

    /// <summary>Names running processes whose executable is the file WinGet reported it could not remove or replace.</summary>
    internal static string LockingProcesses(string details)
    {
        try
        {
            var m = LockedPathPattern().Match(details);
            if (!m.Success) return string.Empty;
            var path = m.Groups[1].Value;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try { if (string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) names.Add($"{p.ProcessName}.exe (PID {p.Id})"); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
            return string.Join(", ", names);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return string.Empty; }
    }

    [GeneratedRegex(@"Access is denied\.?:? *""([^""]+\.exe)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockedPathPattern();

    private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];
}
