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

    public bool ProvidesDisplayNames => true;

    /// <summary>
    /// <c>choco search --limit-output</c> prints only IDs; the package titles are only in the detailed listing,
    /// which is many times slower, so it runs separately after the fast results are shown.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var result = await _runner.RunAsync(new ProcessSpec(_exe,
            ["search", query, "--detailed", "--no-color"], TimeSpan.FromMinutes(5)), cancellationToken).ConfigureAwait(false);
        if (!result.TimedOut && result.ExitCode == 2) return new Dictionary<string, string>();
        PackageManagerOutput.RequireSuccess(result, "Read Chocolatey package names");
        return ParseDisplayNames(result.StdOut);
    }

    /// <summary>
    /// Reads "id version [status]" headers that are directly followed by a " Title: … | Published: …" line.
    /// Requiring the Title line keeps description text that happens to look like a header from being misread.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseDisplayNames(string output)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            var header = lines[i];
            if (header.Length == 0 || char.IsWhiteSpace(header[0])) continue;
            var next = lines[i + 1].TrimStart();
            if (!next.StartsWith("Title:", StringComparison.Ordinal)) continue;
            var parts = header.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IdPattern().IsMatch(parts[0]) || !char.IsAsciiDigit(parts[1][0])) continue;
            var title = next["Title:".Length..];
            var pipe = title.IndexOf(" | ", StringComparison.Ordinal);
            if (pipe >= 0) title = title[..pipe];
            title = title.Trim();
            if (title.Length is 0 or > 200 || title.Any(char.IsControl)) continue;
            names.TryAdd(parts[0], title);
        }
        return names;
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
