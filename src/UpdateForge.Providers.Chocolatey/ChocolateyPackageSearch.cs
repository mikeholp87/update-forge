using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Chocolatey;

public sealed partial class ChocolateyProvider : IPackageSearchProvider
{
    public Task<ProviderAvailability> CheckSearchAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_exe) ? new ProviderAvailability(ProviderHealth.Available, null, null)
            : ProviderAvailability.NotInstalled("Chocolatey is not installed."));
    }

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

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IdPattern().IsMatch(package.PackageId))
            throw new ArgumentException("Chocolatey package ID failed validation.", nameof(package));
        var result = await _runner.RunAsync(new ProcessSpec(_exe,
            ["info", package.PackageId, "--no-color"], TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(result, "Read Chocolatey package details");
        return ParsePackageDetails(result.StdOut);
    }

    public static PackageDetails ParsePackageDetails(string output)
    {
        // Drop the trailing "N packages found." summary so it is not appended to the last multi-line field.
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.TrimEnd().EndsWith(" packages found.", StringComparison.OrdinalIgnoreCase)
                && !l.TrimEnd().EndsWith(" package found.", StringComparison.OrdinalIgnoreCase));
        var fields = PackageDetailsText.Parse(string.Join('\n', lines), ["Description", "Release Notes", "Summary"], splitPipes: true);
        return PackageDetails.From(fields);
    }
}
