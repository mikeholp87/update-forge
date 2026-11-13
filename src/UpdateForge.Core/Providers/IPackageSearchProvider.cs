using UpdateForge.Core.Models;

namespace UpdateForge.Core.Providers;

/// <summary>Searches the manager's configured catalogs, independently of installed inventory.</summary>
public interface IPackageSearchProvider
{
    Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken);
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
