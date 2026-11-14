using System.Text.RegularExpressions;

namespace UpdateForge.Core.Providers;

/// <summary>npm-registry package names, shared by npm, pnpm, Bun and Yarn.</summary>
public static partial class NpmPackageName
{
    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~\-]{0,213}/)?[a-z0-9][a-z0-9._~\-]{0,213}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    private static readonly string[] ArchiveSuffixes = [".tgz", ".tar", ".gz", ".zip"];

    /// <summary>
    /// Scoped or plain name that starts with a letter or digit, so it can never be read as an option. Names ending in an archive
    /// extension are refused: the tools install <c>x.tgz</c> from a local file of that name rather than from the registry.
    /// </summary>
    public static bool IsValid(string? name) =>
        name is { Length: <= 214 } && Pattern().IsMatch(name) && !ArchiveSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}
