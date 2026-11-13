using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;

namespace UpdateForge.Application;

public sealed record PackageSearchProviderResult(string ProviderId, string DisplayName,
    IReadOnlyList<PackageSearchResult> Packages, string? Error);

public sealed record PackageSearchResponse(IReadOnlyList<PackageSearchResult> Packages,
    IReadOnlyList<PackageSearchProviderResult> Providers);

public sealed class PackageSearchService(ProviderRegistry registry, ISettingsStore settingsStore)
{
    public async Task<PackageSearchResponse> SearchAsync(string query, CancellationToken ct)
    {
        query = PackageSearchQuery.Validate(query);
        ct.ThrowIfCancellationRequested();
        // Read saved manager settings on every search so enabling/disabling takes effect immediately.
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var providers = registry.All.Where(p => p is IPackageSearchProvider
            && settings.EnabledProviders.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.DisplayName).ToList();
        var results = await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var availability = await provider.CheckAvailabilityAsync(ct).ConfigureAwait(false);
                if (!availability.IsAvailable)
                    return new PackageSearchProviderResult(provider.Id, provider.DisplayName, [], availability.Message ?? "Package manager is unavailable.");
                var packages = await ((IPackageSearchProvider)provider).SearchPackagesAsync(query, ct).ConfigureAwait(false);
                return new PackageSearchProviderResult(provider.Id, provider.DisplayName, packages, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new PackageSearchProviderResult(provider.Id, provider.DisplayName, [], ex.Message); }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Keep identities from different managers and sources separate.
        var all = results.SelectMany(r => r.Packages)
            .DistinctBy(p => (p.ProviderId.ToUpperInvariant(), p.PackageId.ToUpperInvariant(), p.Source?.ToUpperInvariant()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProviderId).ThenBy(p => p.Source).ToList();
        return new PackageSearchResponse(all, results);
    }
}
