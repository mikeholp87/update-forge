using System.Runtime.CompilerServices;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;

namespace UpdateForge.Application;

public sealed record PackageSearchProviderResult(string ProviderId, string DisplayName,
    IReadOnlyList<PackageSearchResult> Packages, string? Error, bool IsCached = false);

public sealed record PackageSearchResponse(IReadOnlyList<PackageSearchResult> Packages,
    IReadOnlyList<PackageSearchProviderResult> Providers);

public sealed class PackageSearchService(ProviderRegistry registry, ISettingsStore settingsStore, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _cacheLock = new();
    private readonly Dictionary<(string Provider, string Query), CachedSearch> _searches = [];
    private readonly Dictionary<string, CachedAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Provider, string Package, string Source), CachedDetails> _details = [];
    private const int MaxCachedQueries = 32;
    private const int MaxCachedDetails = 64;
    private readonly Dictionary<(string Provider, string Query), CachedNames> _names = [];

    /// <summary>True when the manager returns IDs from search and can fill in display names afterwards.</summary>
    public bool ProvidesDisplayNames(string providerId) =>
        registry.All.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)) is IPackageSearchProvider { ProvidesDisplayNames: true };

    /// <summary>Display names (keyed by package ID) for a query's results, from a manager's slower detailed listing.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(string providerId, string query, CancellationToken ct)
    {
        query = PackageSearchQuery.Validate(query);
        if (registry.All.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)) is not IPackageSearchProvider { ProvidesDisplayNames: true } provider)
            return new Dictionary<string, string>();
        var key = (provider.Id.ToUpperInvariant(), query.ToUpperInvariant());
        long generation;
        lock (_cacheLock)
        {
            generation = _generations.GetValueOrDefault(provider.Id);
            if (_names.TryGetValue(key, out var cached) && cached.ExpiresAt > _clock.GetUtcNow()) return cached.Value;
        }
        var names = await provider.GetDisplayNamesAsync(query, ct).ConfigureAwait(false);
        lock (_cacheLock)
        {
            if (generation == _generations.GetValueOrDefault(provider.Id))
            {
                if (_names.Count >= MaxCachedQueries) _names.Remove(_names.MinBy(p => p.Value.ExpiresAt).Key);
                _names[key] = new(names, _clock.GetUtcNow().AddMinutes(10));
            }
        }
        return names;
    }

    /// <summary>Reads catalog details for one search result from the manager that found it.</summary>
    /// <returns>Null when that manager cannot describe packages.</returns>
    public async Task<PackageDetails?> GetDetailsAsync(PackageSearchResult package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        var provider = registry.All.FirstOrDefault(p => p.Id.Equals(package.ProviderId, StringComparison.OrdinalIgnoreCase)) as IPackageSearchProvider
            ?? throw new InvalidOperationException($"No package manager named “{package.ProviderId}” is available.");
        var key = (provider.Id.ToUpperInvariant(), package.PackageId.ToUpperInvariant(), package.Source?.ToUpperInvariant() ?? "");
        long generation;
        lock (_cacheLock)
        {
            generation = _generations.GetValueOrDefault(provider.Id);
            if (_details.TryGetValue(key, out var cached) && cached.ExpiresAt > _clock.GetUtcNow()) return cached.Value;
        }
        var details = await provider.GetPackageDetailsAsync(package, ct).ConfigureAwait(false);
        lock (_cacheLock)
        {
            if (details is not null && generation == _generations.GetValueOrDefault(provider.Id))
            {
                if (_details.Count >= MaxCachedDetails) _details.Remove(_details.MinBy(p => p.Value.ExpiresAt).Key);
                _details[key] = new(details, _clock.GetUtcNow().AddMinutes(10));
            }
        }
        return details;
    }

    public async Task<PackageSearchResponse> SearchAsync(string query, CancellationToken ct, bool refresh = false)
    {
        var results = new List<PackageSearchProviderResult>();
        await foreach (var result in SearchProvidersAsync(query, ct, refresh).ConfigureAwait(false)) results.Add(result);
        return new PackageSearchResponse(Normalize(results.SelectMany(r => r.Packages)),
            [.. results.OrderBy(r => r.DisplayName)]);
    }

    /// <summary>Yields completed managers immediately, so slow repositories never hold back other results.</summary>
    public async IAsyncEnumerable<PackageSearchProviderResult> SearchProvidersAsync(string query,
        [EnumeratorCancellation] CancellationToken ct, bool refresh = false)
    {
        query = PackageSearchQuery.Validate(query);
        ct.ThrowIfCancellationRequested();
        // Read saved manager settings on every search so enabling/disabling takes effect immediately.
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var providers = registry.All.Where(p => p is IPackageSearchProvider
            && settings.EnabledProviders.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.DisplayName).ToList();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = providers.Select(provider => SearchProviderAsync(provider, query, refresh, stop.Token)).ToList();
        try
        {
            await foreach (var task in Task.WhenEach(tasks).WithCancellation(stop.Token).ConfigureAwait(false))
            {
                var result = await task.ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                yield return result;
            }
        }
        finally { await stop.CancelAsync().ConfigureAwait(false); }
    }

    /// <summary>Discard results after manager/source configuration changes, including in-flight cache writes.</summary>
    public void Invalidate(string? providerId = null)
    {
        lock (_cacheLock)
        {
            if (providerId is null)
            {
                foreach (var provider in registry.All) _generations[provider.Id] = _generations.GetValueOrDefault(provider.Id) + 1;
                _searches.Clear(); _availability.Clear(); _details.Clear(); _names.Clear(); return;
            }
            _generations[providerId] = _generations.GetValueOrDefault(providerId) + 1;
            foreach (var key in _searches.Keys.Where(k => k.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToList())
                _searches.Remove(key);
            _availability.Remove(providerId);
            foreach (var key in _details.Keys.Where(k => k.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToList())
                _details.Remove(key);
            foreach (var key in _names.Keys.Where(k => k.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToList())
                _names.Remove(key);
        }
    }

    private async Task<PackageSearchProviderResult> SearchProviderAsync(IUpdateProvider provider, string query, bool refresh, CancellationToken ct)
    {
        var key = (provider.Id.ToUpperInvariant(), query.ToUpperInvariant());
        long generation;
        ProviderAvailability? availability = null;
        lock (_cacheLock)
        {
            generation = _generations.GetValueOrDefault(provider.Id);
            if (refresh)
            {
                _generations[provider.Id] = ++generation;
                foreach (var stale in _searches.Keys.Where(k => k.Provider == key.Item1).ToList()) _searches.Remove(stale);
                foreach (var stale in _names.Keys.Where(k => k.Provider == key.Item1).ToList()) _names.Remove(stale);
                _availability.Remove(provider.Id);
            }
            if (!refresh)
            {
                if (_searches.TryGetValue(key, out var cached) && cached.ExpiresAt > _clock.GetUtcNow())
                    return cached.Result with { IsCached = true };
                if (_availability.TryGetValue(provider.Id, out var known) && known.ExpiresAt > _clock.GetUtcNow())
                    availability = known.Value;
            }
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (availability is null)
            {
                availability = await ((IPackageSearchProvider)provider).CheckSearchAvailabilityAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_cacheLock)
                {
                    if (generation == _generations.GetValueOrDefault(provider.Id))
                        _availability[provider.Id] = new(availability, _clock.GetUtcNow().AddSeconds(availability.IsAvailable ? 60 : 15));
                }
            }
            if (!availability.IsAvailable)
            {
                lock (_cacheLock)
                {
                    if (generation == _generations.GetValueOrDefault(provider.Id))
                        foreach (var stale in _searches.Keys.Where(k => k.Provider == key.Item1).ToList()) _searches.Remove(stale);
                }
                return new(provider.Id, provider.DisplayName, [], availability.Message ?? "Package manager is unavailable.");
            }
            var packages = await ((IPackageSearchProvider)provider).SearchPackagesAsync(query, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = new PackageSearchProviderResult(provider.Id, provider.DisplayName, Normalize(packages), null);
            lock (_cacheLock)
            {
                if (generation == _generations.GetValueOrDefault(provider.Id))
                {
                    // Bound memory even when the user makes many distinct searches.
                    if (_searches.Count >= MaxCachedQueries) _searches.Remove(_searches.MinBy(p => p.Value.ExpiresAt).Key);
                    _searches[key] = new(result, _clock.GetUtcNow().AddMinutes(2));
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed fresh search must not leave older successes or health checks masking the error.
            lock (_cacheLock)
            {
                if (generation == _generations.GetValueOrDefault(provider.Id)) { _searches.Remove(key); _availability.Remove(provider.Id); }
            }
            return new(provider.Id, provider.DisplayName, [], ex.Message);
        }
    }

    private static List<PackageSearchResult> Normalize(IEnumerable<PackageSearchResult> packages) =>
        // Keep identities from different managers and sources separate.
        packages
            .DistinctBy(p => (p.ProviderId.ToUpperInvariant(), p.PackageId.ToUpperInvariant(), p.Source?.ToUpperInvariant()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProviderId).ThenBy(p => p.Source).ToList();

    private sealed record CachedSearch(PackageSearchProviderResult Result, DateTimeOffset ExpiresAt);
    private sealed record CachedNames(IReadOnlyDictionary<string, string> Value, DateTimeOffset ExpiresAt);
    private sealed record CachedDetails(PackageDetails Value, DateTimeOffset ExpiresAt);
    private sealed record CachedAvailability(ProviderAvailability Value, DateTimeOffset ExpiresAt);
}
