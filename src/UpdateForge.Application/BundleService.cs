using Microsoft.Extensions.Logging;
using UpdateForge.Core.Bundles;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;

namespace UpdateForge.Application;

public sealed record BundlePlan(IReadOnlyList<BundlePackage> ToInstall, IReadOnlyList<BundlePackage> AlreadyInstalled);

public sealed record BundleInstallResult(BundlePackage Package, JobState State, string Message);

/// <summary>
/// Package bundles: export what is installed through WinGet, and on another machine (or after a reinstall) install whatever from a
/// bundle is missing. Installation goes through the provider's validated install path and is recorded in history.
/// </summary>
public sealed class BundleService(ProviderRegistry registry, IHistoryStore history, IEngineLock engineLock, ILogger<BundleService> log)
{
    public const string ProviderKey = "winget";

    /// <summary>Everything WinGet knows as installed, from an approved source, with an exact package identity.</summary>
    public static PackageBundle CreateBundle(IEnumerable<InstalledApplication> apps)
    {
        var packages = apps
            .Where(a => a.ProviderId == ProviderKey && PackageBundleFormat.IsValidId(a.PackageId) && PackageBundleFormat.IsAllowedSource(a.PackageSource))
            .GroupBy(a => a.PackageId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new BundlePackage(a.PackageId!, a.PackageSource!.ToLowerInvariant(), a.Name,
                RealVersion(a.InstalledVersion)))
            .ToList();
        return new PackageBundle(PackageBundleFormat.CurrentSchema, DateTimeOffset.UtcNow, "UpdateForge", packages);
    }

    // WinGet reports "Unknown" when it cannot read a version; that is not something to pin an install to.
    private static string? RealVersion(string? v) =>
        PackageBundleFormat.IsValidVersion(v) && !v!.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? v : null;

    public static BundlePlan Plan(PackageBundle bundle, IEnumerable<InstalledApplication> installed)
    {
        var have = installed.Where(a => a.PackageId is not null).Select(a => a.PackageId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = bundle.Packages.Where(p => !have.Contains(p.Id)).ToList();
        var present = bundle.Packages.Where(p => have.Contains(p.Id)).ToList();
        return new BundlePlan(missing, present);
    }

    /// <summary>Installs the missing packages one at a time. Stops starting new installs when <paramref name="stop"/> is canceled.</summary>
    public async Task<IReadOnlyList<BundleInstallResult>> InstallMissingAsync(
        BundlePlan plan, bool exactVersions, IProgress<string>? progress, CancellationToken stop, CancellationToken hardCancel = default)
    {
        using var held = engineLock.TryAcquire() ?? throw new EngineBusyException();
        var provider = registry.Find(ProviderKey);
        var results = new List<BundleInstallResult>();
        var total = plan.ToInstall.Count;

        for (var i = 0; i < total; i++)
        {
            var pkg = plan.ToInstall[i];
            var label = pkg.Name ?? pkg.Id;
            if (stop.IsCancellationRequested) { results.Add(new(pkg, JobState.Canceled, "Canceled before starting.")); continue; }
            if (provider is null || !provider.Capabilities.CanUpdate) { results.Add(new(pkg, JobState.ManualActionRequired, "WinGet is not available.")); continue; }

            progress?.Report($"[{i + 1}/{total}] Installing {label}…");
            var started = DateTimeOffset.UtcNow;
            UpdateResult result;
            try
            {
                var request = new InstallRequest(pkg.Id, pkg.Source, exactVersions ? pkg.Version : null);
                result = await provider.InstallAsync(request, new Progress<UpdateProgress>(), hardCancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { result = new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, Message = "Canceled." }; }
            catch (Exception ex)
            {
                log.LogError(ex, "Provider threw while installing {Package}", pkg.Id);
                result = UpdateResult.Fail(ErrorCategory.Unknown, "Unexpected provider error: " + ex.Message);
            }

            var state = PackageInstallService.StateFor(result.Outcome);
            var message = "From bundle: " + (result.Message ?? state.ToString());
            await history.AddAsync(new HistoryRecord(0, DateTimeOffset.UtcNow, $"{ProviderKey}:{pkg.Id}", label, ProviderKey, null,
                exactVersions ? pkg.Version : null, null, state, DateTimeOffset.UtcNow - started, result.ExitCode, result.Error,
                result.RestartRequired || state == JobState.RestartRequired, message, result.TechnicalDetails), CancellationToken.None).ConfigureAwait(false);
            results.Add(new(pkg, state, result.Message ?? state.ToString()));
        }
        return results;
    }
}
