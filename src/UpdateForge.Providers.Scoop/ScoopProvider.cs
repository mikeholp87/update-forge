using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Scoop;

public sealed record ScoopApp(string Name, string Version, string Source, bool Held, bool Global);

public static class ScoopParsing
{
    /// <summary>Parses <c>scoop export</c> JSON (apps[].Name/Version/Source/Info).</summary>
    public static IReadOnlyList<ScoopApp> ParseExport(string json)
    {
        var list = new List<ScoopApp>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("apps", out var apps)) return list;
        foreach (var a in apps.EnumerateArray())
        {
            var name = a.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var info = a.TryGetProperty("Info", out var i) ? i.GetString() ?? "" : "";
            list.Add(new ScoopApp(name,
                a.TryGetProperty("Version", out var v) ? v.GetString() ?? "Unknown" : "Unknown",
                a.TryGetProperty("Source", out var s) ? s.GetString() ?? "" : "",
                info.Contains("Held", StringComparison.OrdinalIgnoreCase),
                info.Contains("Global", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>
    /// Parses <c>scoop status</c> rows by anchoring on app names already known from the export:
    /// the first token is the name, followed by the installed and latest version.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseStatusLatest(string output, IReadOnlySet<string> knownNames)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Columns: Name, Installed Version, Latest Version, ... (English-only output; names are anchored on the export).
            if (tokens.Length >= 3 && knownNames.Contains(tokens[0])) latest[tokens[0]] = tokens[2];
        }
        return latest;
    }
}

/// <summary>
/// Scoop provider. Scoop is a PowerShell tool, so it is invoked as <c>powershell.exe -NoProfile -File scoop.ps1 …</c>
/// with typed arguments and no execution-policy bypass. Held packages are reported as pinned and never updated.
/// Not verified against a live Scoop installation in this repository's CI; see docs/PROVIDERS.md.
/// </summary>
public sealed partial class ScoopProvider : IUpdateProvider
{
    public const string ProviderKey = "scoop";
    private readonly IProcessRunner _runner;
    private readonly ILogger<ScoopProvider> _log;
    private readonly string _scoopScript;
    private readonly string _powershell;

    public ScoopProvider(IProcessRunner runner, ILogger<ScoopProvider> log, string? scoopScript = null, string? powershell = null)
    {
        _runner = runner;
        _log = log;
        var root = Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } s ? s : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");
        _scoopScript = scoopScript ?? Path.Combine(root, "shims", "scoop.ps1");
        _powershell = powershell ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public string Id => ProviderKey;
    public string DisplayName => "Scoop";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, SupportsPins: true);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private ProcessSpec Spec(TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(_powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", _scoopScript, .. args], timeout, onLine);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_scoopScript) || !File.Exists(_powershell)) return ProviderAvailability.Unavailable("Scoop is not installed.");
        var r = await _runner.RunAsync(Spec(TimeSpan.FromSeconds(60), null, "--version"), cancellationToken).ConfigureAwait(false);
        return r.ExitCode == 0 ? new ProviderAvailability(ProviderHealth.Available, r.StdOut.Trim(), null) : ProviderAvailability.Unavailable("Scoop did not respond.");
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var export = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "export"), cancellationToken).ConfigureAwait(false);
        var apps = ScoopParsing.ParseExport(export.StdOut);
        var status = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "status"), cancellationToken).ConfigureAwait(false);
        var latest = ScoopParsing.ParseStatusLatest(status.StdOut, apps.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var now = DateTimeOffset.UtcNow;
        return [.. apps.Select(a =>
        {
            latest.TryGetValue(a.Name, out var avail);
            var newer = avail is not null && AppVersion.IsNewer(a.Version, avail);
            return new InstalledApplication
            {
                Id = $"{ProviderKey}:{a.Name}",
                Name = a.Name,
                ProviderId = ProviderKey,
                PackageId = a.Name,
                PackageSource = a.Source,
                InstalledVersion = a.Version,
                AvailableVersion = newer ? avail : null,
                Status = newer ? UpdateStatus.UpdateAvailable : AppVersion.TryParse(a.Version, out _, out _) ? UpdateStatus.UpToDate : UpdateStatus.Unknown,
                Support = a.Global ? UpdateSupport.ApprovalRequired : UpdateSupport.AutoUpdatable,
                Scope = a.Global ? InstallScope.Machine : InstallScope.User,
                RequiresElevation = a.Global,
                IsPinned = a.Held,
                Note = a.Held ? "Held in Scoop; UpdateForge will not update it." : a.Global ? "Global Scoop install; requires administrator." : null,
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, a.Name, a.Source)],
            };
        })];
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        if (app.ProviderId != ProviderKey || app.PackageId is null || !NamePattern().IsMatch(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Scoop package name failed validation; refusing to run.");
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Package is held in Scoop." };
        if (app.RequiresElevation) return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, Message = "Global Scoop packages need an administrator prompt." };

        _log.LogInformation("Updating Scoop package {Package}", app.PackageId);
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(30), l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }, "update", app.PackageId), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "Scoop timed out.");
        var tail = r.StdOut.Length > 4000 ? r.StdOut[^4000..] : r.StdOut;
        return r.ExitCode == 0
            ? new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = "Updated with Scoop.", TechnicalDetails = tail }
            : UpdateResult.Fail(ErrorCategory.InstallerFailed, $"Scoop exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        var export = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "export"), cancellationToken).ConfigureAwait(false);
        return ScoopParsing.ParseExport(export.StdOut).FirstOrDefault(a => string.Equals(a.Name, application.PackageId, StringComparison.OrdinalIgnoreCase))?.Version;
    }
}
