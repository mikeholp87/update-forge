using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateForge.Core.Recommendations;
using UpdateForge.Core.Storage;

namespace UpdateForge.Providers.Linux;

public static partial class FlathubParsing
{
    private const long MinInstalls = 1000;

    /// <summary>
    /// Apps from a Flathub category listing (already ordered by installs). Only free-software desktop apps with a valid app id and a real
    /// following are kept, and every text field is cleaned because it comes from outside.
    /// </summary>
    public static IReadOnlyList<DiscoveredPackage> ParseCategory(string json, string niche, int max)
    {
        var list = new List<DiscoveredPackage>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return list; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return list;
            foreach (var hit in hits.EnumerateArray())
            {
                if (list.Count >= max) break;
                if (hit.ValueKind != JsonValueKind.Object) continue;
                var id = Str(hit, "app_id");
                if (id is null || !AppId().IsMatch(id) || Str(hit, "type") != "desktop-application") continue;
                if (!hit.TryGetProperty("is_free_license", out var free) || free.ValueKind != JsonValueKind.True) continue;
                var installs = hit.TryGetProperty("installs_last_month", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var v) ? v : 0;
                if (installs < MinInstalls) continue;
                var name = DiscoveryText.Clean(Str(hit, "name"), 60);
                if (name.Length == 0) continue;
                list.Add(new DiscoveredPackage("flatpak", id, name, DiscoveryText.Clean(Str(hit, "summary"), 140), niche, DiscoveryOrigin.Online, [], "flathub", installs));
            }
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+){2,}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex AppId();
}

/// <summary>
/// Opt-in: asks Flathub's public API which free apps are most installed in the categories the user is into. The request carries a category
/// name and nothing else: no list of installed software, no identifier. Answers are cached for a day. Installing them still goes through the
/// normal confirmed path.
/// </summary>
public sealed class FlathubDiscovery : IDiscoverySource
{
    private const string Endpoint = "https://flathub.org/api/v2/collection/category/";
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(24);
    private static readonly HttpClient Shared = Create();

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _clock;

    public FlathubDiscovery() : this(null, null, null) { }

    internal FlathubDiscovery(HttpClient? http, string? cacheDirectory, TimeProvider? clock)
    {
        _http = http ?? Shared;
        _cacheDirectory = cacheDirectory ?? Path.Combine(PrivateStorage.DataDirectory, "cache");
        _clock = clock ?? TimeProvider.System;
    }

    public bool RequiresNetwork => true;

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 2_000_000 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateForge");
        return http;
    }

    public async Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(DiscoveryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new List<DiscoveredPackage>();
        foreach (var niche in request.Niches)
        {
            var category = CategoryNiches.FlathubCategory(niche);
            if (category is null) continue;
            var json = await LoadAsync(category, ct).ConfigureAwait(false);
            if (json is not null) result.AddRange(FlathubParsing.ParseCategory(json, niche, 12));
        }
        return result;
    }

    private async Task<string?> LoadAsync(string category, CancellationToken ct)
    {
        var file = Path.Combine(_cacheDirectory, "flathub-" + category + ".json");
        try
        {
            if (File.Exists(file) && _clock.GetUtcNow() - File.GetLastWriteTimeUtc(file) < CacheLife)
                return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable cache: fetch again */ }

        string body;
        try
        {
            using var response = await _http.GetAsync(new Uri($"{Endpoint}{category}?page=1&per_page=30"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { return null; }                                    // offline: simply no online suggestions
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }   // the request timed out

        try
        {
            PrivateStorage.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(file, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache is an optimization */ }
        return body;
    }
}
