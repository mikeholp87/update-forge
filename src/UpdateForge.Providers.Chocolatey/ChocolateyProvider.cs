using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Chocolatey;

public sealed record ChocoPackage(string Id, string Version);
public sealed record ChocoOutdated(string Id, string Current, string Available, bool Pinned);

public static class ChocolateyParsing
{
    /// <summary>Parses <c>choco list --local-only --limit-output</c>: <c>id|version</c> per line.</summary>
    public static IReadOnlyList<ChocoPackage> ParseList(string output) =>
        [.. Lines(output).Select(l => l.Split('|')).Where(p => p.Length >= 2 && p[0].Length > 0).Select(p => new ChocoPackage(p[0], p[1]))];

    /// <summary>Parses <c>choco outdated --limit-output</c>: <c>id|current|available|pinned</c>.</summary>
    public static IReadOnlyList<ChocoOutdated> ParseOutdated(string output) =>
        [.. Lines(output).Select(l => l.Split('|')).Where(p => p.Length >= 4)
            .Select(p => new ChocoOutdated(p[0], p[1], p[2], string.Equals(p[3], "true", StringComparison.OrdinalIgnoreCase)))];

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Contains('|'));
}

/// <summary>
/// Chocolatey provider using its machine-readable <c>--limit-output</c> mode. Most Chocolatey installs need administrator
/// rights, so UpdateForge only runs upgrades when it is already running elevated; otherwise the update is reported as
/// needing approval and never attempted. Pinned packages are never upgraded.
/// Not verified against a live Chocolatey installation in this repository's CI; see docs/PROVIDERS.md.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ChocolateyProvider : IUpdateProvider
{
    public const string ProviderKey = "chocolatey";
    private readonly IProcessRunner _runner;
    private readonly ILogger<ChocolateyProvider> _log;
    private readonly string _exe;
    private readonly Func<bool> _isElevated;

    public ChocolateyProvider(IProcessRunner runner, ILogger<ChocolateyProvider> log, string? exePath = null, Func<bool>? isElevated = null)
    {
        _runner = runner;
        _log = log;
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        _exe = exePath ?? Path.Combine(root, "bin", "choco.exe");
        _isElevated = isElevated ?? (() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
    }

    public string Id => ProviderKey;
    public string DisplayName => "Chocolatey";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, SupportsPins: true);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_exe)) return ProviderAvailability.NotInstalled("Chocolatey is not installed.");
        var r = await _runner.RunAsync(new ProcessSpec(_exe, ["--version"], TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false);
        return r.ExitCode == 0 ? new ProviderAvailability(ProviderHealth.Available, r.StdOut.Trim(), null) : ProviderAvailability.Unavailable("Chocolatey did not respond.");
    }

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var list = await _runner.RunAsync(new ProcessSpec(_exe, ["list", "--local-only", "--limit-output", "--no-color"], TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        var outdated = await _runner.RunAsync(new ProcessSpec(_exe, ["outdated", "--limit-output", "--ignore-pinned=false", "--no-color"], TimeSpan.FromMinutes(3)), cancellationToken).ConfigureAwait(false);
        var out_ = ChocolateyParsing.ParseOutdated(outdated.StdOut).ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        var elevated = _isElevated();
        var now = DateTimeOffset.UtcNow;

        return [.. ChocolateyParsing.ParseList(list.StdOut).Select(p =>
        {
            out_.TryGetValue(p.Id, out var o);
            var newer = o is not null && AppVersion.IsNewer(p.Version, o.Available);
            return new InstalledApplication
            {
                Id = $"{ProviderKey}:{p.Id}",
                Name = p.Id,
                ProviderId = ProviderKey,
                PackageId = p.Id,
                PackageSource = "chocolatey",
                InstalledVersion = p.Version,
                AvailableVersion = newer ? o!.Available : null,
                Status = newer ? UpdateStatus.UpdateAvailable : AppVersion.TryParse(p.Version, out _, out _) ? UpdateStatus.UpToDate : UpdateStatus.Unknown,
                Support = elevated ? UpdateSupport.AutoUpdatable : UpdateSupport.ApprovalRequired,
                Scope = InstallScope.Machine,
                RequiresElevation = !elevated,
                IsPinned = o?.Pinned ?? false,
                Note = o?.Pinned == true ? "Pinned in Chocolatey; UpdateForge will not update it."
                    : !elevated ? "Chocolatey upgrades need administrator rights. Run UpdateForge as administrator or upgrade from an elevated terminal." : null,
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, p.Id)],
            };
        })];
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        if (app.ProviderId != ProviderKey || app.PackageId is null || !IdPattern().IsMatch(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "Chocolatey package id failed validation; refusing to run.");
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Package is pinned in Chocolatey." };
        if (!_isElevated())
            return new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, Message = "Chocolatey upgrades need administrator rights; UpdateForge did not elevate." };

        _log.LogInformation("Upgrading Chocolatey package {Package}", app.PackageId);
        // No --force, no --ignore-checksums, no --allow-empty-checksums: integrity checks are never weakened.
        var r = await _runner.RunAsync(new ProcessSpec(_exe, ["upgrade", app.PackageId, "--yes", "--no-progress", "--limit-output", "--no-color"], TimeSpan.FromMinutes(30),
            l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); }), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "Chocolatey timed out.");
        var tail = r.StdOut.Length > 4000 ? r.StdOut[^4000..] : r.StdOut;
        return r.ExitCode switch
        {
            0 => new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = "Upgraded with Chocolatey.", TechnicalDetails = tail },
            1604 or 3010 => new UpdateResult { Outcome = UpdateOutcome.RestartRequired, ExitCode = r.ExitCode, RestartRequired = true, Message = "Upgraded; a restart is required.", TechnicalDetails = tail },
            _ => UpdateResult.Fail(ErrorCategory.InstallerFailed, $"Chocolatey exited with code {r.ExitCode}.", r.ExitCode, tail),
        };
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (application.PackageId is null || !IdPattern().IsMatch(application.PackageId)) return null;
        var r = await _runner.RunAsync(new ProcessSpec(_exe, ["list", application.PackageId, "--local-only", "--exact", "--limit-output", "--no-color"], TimeSpan.FromMinutes(1)), cancellationToken).ConfigureAwait(false);
        var found = ChocolateyParsing.ParseList(r.StdOut);
        return found.Count > 0 ? found[0].Version : null;
    }
}
