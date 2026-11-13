using UpdateForge.Core.Models;

namespace UpdateForge.Core.Providers;

/// <summary>Searches the manager's configured catalogs, independently of installed inventory.</summary>
public interface IPackageSearchProvider : IUpdateProvider
{
    /// <summary>Search only needs to know whether the manager can be started; it does not need its version.</summary>
    Task<ProviderAvailability> CheckSearchAvailabilityAsync(CancellationToken cancellationToken) =>
        CheckAvailabilityAsync(cancellationToken);

    Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken);

    /// <summary>Reads catalog metadata for one search result. Returns null when the manager cannot describe packages.</summary>
    Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken) =>
        Task.FromResult<PackageDetails?>(null);
}

public static class PackageSearchQuery
{
    public static string Validate(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var value = query.Trim();
        if (value.Length is 0 or > 256 || value[0] == '-' || query.Any(char.IsControl))
            throw new ArgumentException("Enter a search of 1–256 characters that does not start with a dash or contain control characters.", nameof(query));
        return value;
    }
}
