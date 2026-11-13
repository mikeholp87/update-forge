using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Chocolatey;

public sealed partial class ChocolateyProvider : IPackageSearchProvider
{
    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        // Omitting --source uses all enabled sources in Chocolatey's own configuration.
        var result = await _runner.RunAsync(new ProcessSpec(_exe,
            ["search", query, "--limit-output", "--no-color"], TimeSpan.FromMinutes(3)), cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut && result.ExitCode == 2) return [];
        PackageManagerOutput.RequireSuccess(result, "Search Chocolatey packages");
        return [.. ChocolateyParsing.ParseList(result.StdOut)
            .Where(p => IdPattern().IsMatch(p.Id) && !string.IsNullOrWhiteSpace(p.Version))
            .Select(p => new PackageSearchResult(ProviderKey, p.Id, p.Id, p.Version, null))];
    }
}
