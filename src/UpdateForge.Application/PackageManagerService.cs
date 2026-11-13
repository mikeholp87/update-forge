using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;

namespace UpdateForge.Application;

public sealed record PackageManagerStatus(string Id, string DisplayName, bool Enabled,
    ProviderAvailability Availability, ProviderCapabilities Capabilities, string? ExecutablePath,
    bool SupportsSources, bool CanRefreshSources);

public sealed class PackageManagerService(ProviderRegistry registry, ISettingsStore settingsStore, IEngineLock engineLock,
    PackageSearchService? search = null)
{
    public async Task<IReadOnlyList<PackageManagerStatus>> GetStatusAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return await Task.WhenAll(registry.All.OrderBy(p => p.DisplayName).Select(async provider =>
        {
            ProviderAvailability availability;
            try { availability = await provider.CheckAvailabilityAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { availability = ProviderAvailability.Unavailable(ex.Message); }
            var manager = provider as IPackageManager;
            return new PackageManagerStatus(provider.Id, provider.DisplayName,
                settings.EnabledProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase), availability,
                provider.Capabilities, manager?.ExecutablePath, manager is not null, manager?.CanRefreshSources == true);
        })).ConfigureAwait(false);
    }

    public async Task SaveEnabledAsync(IEnumerable<string> enabledIds, CancellationToken ct)
    {
        var known = registry.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.IsSubsetOf(known)) throw new ArgumentException("Unknown package manager.", nameof(enabledIds));
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        // Preserve settings belonging to providers from a newer installation.
        await settingsStore.SaveAsync(settings with
        {
            EnabledProviders = [.. settings.EnabledProviders.Where(id => !known.Contains(id)), .. selected],
        }, ct).ConfigureAwait(false);
        search?.Invalidate();
    }

    public async Task<string> GetSourcesAsync(string id, CancellationToken ct)
    {
        var manager = await RequireAvailableAsync(id, ct).ConfigureAwait(false);
        return await manager.GetSourcesAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> RefreshSourcesAsync(string id, CancellationToken ct)
    {
        using var lease = engineLock.TryAcquire() ?? throw new InvalidOperationException("An update or maintenance operation is already running. Try again after it finishes.");
        var manager = await RequireAvailableAsync(id, ct).ConfigureAwait(false);
        if (!manager.CanRefreshSources) throw new NotSupportedException("This manager fetches sources when checking packages; no refresh is needed.");
        try { return await manager.RefreshSourcesAsync(ct).ConfigureAwait(false); }
        finally { search?.Invalidate(id); }
    }

    private async Task<IPackageManager> RequireAvailableAsync(string id, CancellationToken ct)
    {
        var provider = registry.Find(id) ?? throw new ArgumentException("Unknown package manager.", nameof(id));
        if (provider is not IPackageManager manager) throw new NotSupportedException("This inventory provider has no package sources.");
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (!settings.EnabledProviders.Contains(id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enable this package manager and save changes first.");
        var availability = await provider.CheckAvailabilityAsync(ct).ConfigureAwait(false);
        if (!availability.IsAvailable) throw new InvalidOperationException(availability.Message ?? "Package manager is unavailable.");
        return manager;
    }
}
