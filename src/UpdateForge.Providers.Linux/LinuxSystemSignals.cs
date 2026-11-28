using System.Globalization;
using System.Text.RegularExpressions;
using UpdateForge.Core.Recommendations;

namespace UpdateForge.Providers.Linux;

public static partial class DesktopEntryParsing
{
    /// <summary>
    /// Reads the <c>[Desktop Entry]</c> group of a launcher file. Hidden entries and anything that is not an application are skipped.
    /// Only the unlocalized <c>Name</c> is used. Nothing in the file is ever executed.
    /// </summary>
    public static LauncherEntry? Parse(string text)
    {
        var inEntry = false;
        string? name = null, type = null;
        IReadOnlyList<string> categories = [];
        foreach (var raw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                if (inEntry) break;
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            if (!inEntry) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "Type": type = value; break;
                case "Name": name = DiscoveryText.Clean(value, 80); break;
                case "Categories": categories = [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30)]; break;
                case "NoDisplay" or "Hidden" when value.Equals("true", StringComparison.OrdinalIgnoreCase): return null;
            }
        }
        return type == "Application" && !string.IsNullOrEmpty(name) ? new LauncherEntry(name, categories) : null;
    }

    /// <summary>
    /// Latest <c>install</c> time per package from a dpkg log (<c>2026-11-10 08:23:18 install pkg:arch &lt;none&gt; 1.2</c>), keyed by lower-case name.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ParseDpkgLog(string text)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in LinuxTools.Lines(text))
        {
            var m = InstallLine().Match(line);
            if (!m.Success) continue;
            if (!DateTimeOffset.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)) continue;
            var name = m.Groups["n"].Value.ToLowerInvariant();
            if (!map.TryGetValue(name, out var old) || at > old) map[name] = at;
        }
        return map;
    }

    [GeneratedRegex(@"^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) install (?<n>[a-z0-9][a-z0-9+.\-]*)(?::\S+)? ", RegexOptions.CultureInvariant)]
    private static partial Regex InstallLine();
}

/// <summary>
/// Reads, without running anything: the menu entries on this computer (system, user, Flatpak, Snap) and the dpkg install log.
/// They tell what kind of software the user has even when the catalog has never heard of it, and when it was installed.
/// </summary>
public sealed class LinuxSystemSignals : ISystemSignalSource
{
    private const long MaxFileBytes = 64 * 1024;
    private const int MaxLaunchers = 2000;
    private readonly IReadOnlyList<string> _appDirs;
    private readonly IReadOnlyList<string> _logs;

    public LinuxSystemSignals() : this(DefaultAppDirs(), ["/var/log/dpkg.log", "/var/log/dpkg.log.1"]) { }

    internal LinuxSystemSignals(IReadOnlyList<string> appDirs, IReadOnlyList<string> dpkgLogs)
    {
        _appDirs = appDirs;
        _logs = dpkgLogs;
    }

    private static string[] DefaultAppDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            "/usr/share/applications", "/usr/local/share/applications", "/var/lib/flatpak/exports/share/applications", "/var/lib/snapd/desktop/applications",
            Path.Combine(home, ".local/share/applications"), Path.Combine(home, ".local/share/flatpak/exports/share/applications"),
        ];
    }

    public async Task<SystemSignals> ReadAsync(CancellationToken ct)
    {
        var launchers = new Dictionary<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _appDirs)
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.desktop") : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (launchers.Count >= MaxLaunchers) break;
                var text = await ReadSmallAsync(file, ct).ConfigureAwait(false);
                if (text is not null && DesktopEntryParsing.Parse(text) is { } entry) launchers.TryAdd(entry.Name, entry);
            }
        }

        var installed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var log in _logs)
        {
            try
            {
                if (!File.Exists(log)) continue;
                foreach (var (name, at) in DesktopEntryParsing.ParseDpkgLog(await File.ReadAllTextAsync(log, ct).ConfigureAwait(false)))
                    if (!installed.TryGetValue(name, out var old) || at > old) installed[name] = at;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a log we may not read just means less history */ }
        }
        return new SystemSignals { Launchers = [.. launchers.Values], InstalledAt = installed };
    }

    private static async Task<string?> ReadSmallAsync(string path, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
