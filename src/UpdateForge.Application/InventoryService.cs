using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Inventory;
using UpdateForge.Core.Models;

namespace UpdateForge.Application;

public sealed record ProviderScanStatus(string ProviderId, ProviderAvailability Availability, int ApplicationCount, string? Error);

public sealed record ScanResult(IReadOnlyList<InstalledApplication> Applications, IReadOnlyList<ProviderScanStatus> Providers, TimeSpan Duration, DateTimeOffset StartedAt);

public sealed class InventoryService(
    ProviderRegistry registry,
    IInventoryStore inventoryStore,
    IScanHistoryStore scanHistory,
    IExclusionStore exclusions,
    IHistoryStore history,
    ILogger<InventoryService> log)
{
    /// <summary>Runs every enabled provider concurrently. One failing provider never fails the scan.</summary>
    public async Task<ScanResult> ScanAsync(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var enabled = registry.All.Where(p => settings.EnabledProviders.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        var tasks = enabled.Select(p => ScanProviderAsync(p, progress, ct)).ToList();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        var excluded = await exclusions.GetExcludedAsync(ct).ConfigureAwait(false);
        var recent = await history.QueryAsync(null, 500, ct).ConfigureAwait(false);
        var merged = UpgradeEligibility.Apply(
                [.. InventoryMerger.Merge(outcomes.SelectMany(o => o.Apps)).Select(a => excluded.Contains(a.Id) ? a with { IsExcluded = true } : a)],
                recent)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await inventoryStore.SaveSnapshotAsync(merged, ct).ConfigureAwait(false);
        sw.Stop();
        var summary = string.Join("; ", outcomes.Select(o => $"{o.Status.ProviderId}={o.Status.Availability.Health}:{o.Status.ApplicationCount}"));
        await scanHistory.AddAsync(new ScanRecord(started, sw.Elapsed, merged.Count, merged.Count(a => a.UpdateAvailable), summary), ct).ConfigureAwait(false);
        log.LogInformation("Scan finished: {Count} applications in {Elapsed} ms ({Summary})", merged.Count, sw.ElapsedMilliseconds, summary);
        return new ScanResult(merged, outcomes.Select(o => o.Status).ToList(), sw.Elapsed, started);
    }

    private async Task<(ProviderScanStatus Status, IReadOnlyList<InstalledApplication> Apps)> ScanProviderAsync(
        Core.Providers.IUpdateProvider provider, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report($"Scanning {provider.DisplayName}…");
            var availability = await provider.CheckAvailabilityAsync(ct).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                log.LogWarning("Provider {Provider} unavailable: {Message}", provider.Id, availability.Message);
                return (new ProviderScanStatus(provider.Id, availability, 0, availability.Message), []);
            }
            var apps = await provider.DiscoverAsync(ct).ConfigureAwait(false);
            return (new ProviderScanStatus(provider.Id, availability, apps.Count, null), apps);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Provider {Provider} failed during discovery", provider.Id);
            return (new ProviderScanStatus(provider.Id, new ProviderAvailability(ProviderHealth.Degraded, null, ex.Message), 0, ex.Message), []);
        }
    }
}
