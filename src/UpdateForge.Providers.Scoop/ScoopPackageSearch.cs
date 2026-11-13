using System.Text.RegularExpressions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Scoop;

public sealed partial class ScoopProvider : IPackageSearchProvider
{
    public Task<ProviderAvailability> CheckSearchAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_scoopScript) && File.Exists(_powershell) ? new ProviderAvailability(ProviderHealth.Available, null, null)
            : ProviderAvailability.NotInstalled("Scoop is not installed."));
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var pattern = Regex.Escape(query);
        if (!pattern.Equals(query, StringComparison.Ordinal))
        {
            // Cached Scoop search uses literal substrings; the older backend expects a regex.
            var cache = await _runner.RunAsync(Spec(TimeSpan.FromSeconds(30), null,
                "config", "use_sqlite_cache"), cancellationToken).ConfigureAwait(false);
            PackageManagerOutput.RequireSuccess(cache, "Read Scoop search configuration");
            if (cache.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)) pattern = query;
        }
        var result = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(3), null,
            "search", pattern), cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut && result.ExitCode == 1
            && result.StdOut.Contains("No matches found.", StringComparison.OrdinalIgnoreCase)) return [];
        PackageManagerOutput.RequireSuccess(result, "Search Scoop packages");
        return ParsePackageSearch(result.StdOut);
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        // The name and bucket come from search output (remote metadata), so both are validated before they reach a command line.
        if (!NamePattern().IsMatch(package.PackageId))
            throw new ArgumentException("Scoop package name failed validation.", nameof(package));
        if (package.Source is { } bucket && !NamePattern().IsMatch(bucket))
            throw new ArgumentException("Scoop bucket name failed validation.", nameof(package));
        var target = package.Source is { } b ? $"{b}/{package.PackageId}" : package.PackageId;
        var result = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "info", target), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(result, "Read Scoop package details");
        return PackageDetails.From(PackageDetailsText.Parse(result.StdOut));
    }

    public static IReadOnlyList<PackageSearchResult> ParsePackageSearch(string output)
    {
        var packages = new List<PackageSearchResult>();
        var inTable = false;
        string? bucket = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { inTable = false; continue; }
            // Older Scoop versions group results under a quoted bucket name.
            if (line.StartsWith('\'') && line.EndsWith("' bucket:", StringComparison.Ordinal))
            {
                bucket = line[1..^9];
                inTable = false;
                continue;
            }
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                inTable = parts.Length >= 3 && parts[1].Equals("Version", StringComparison.OrdinalIgnoreCase)
                    && parts[2].Equals("Source", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith("----", StringComparison.Ordinal)) continue;
            if (inTable && parts.Length >= 3 && NamePattern().IsMatch(parts[0]) && NamePattern().IsMatch(parts[2]))
                packages.Add(new(ProviderKey, parts[0], parts[0], parts[1], parts[2]));
            else if (bucket is not null && parts.Length >= 2 && NamePattern().IsMatch(parts[0])
                && parts[1].StartsWith('(') && parts[1].EndsWith(')'))
                packages.Add(new(ProviderKey, parts[0], parts[0], parts[1][1..^1], bucket));
        }
        return packages;
    }
}
