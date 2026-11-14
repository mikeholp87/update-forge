using UpdateForge.Core.Processes;

namespace UpdateForge.Providers.Linux;

/// <summary>Shared helpers: tools are looked up in fixed system directories (never the caller's PATH) and queried with a stable locale.</summary>
internal static class LinuxTools
{
    private static readonly string[] Dirs = ["/usr/bin", "/usr/local/bin", "/bin", "/snap/bin", "/usr/sbin", "/sbin"];

    internal static readonly IReadOnlyDictionary<string, string> CLocale = new Dictionary<string, string> { ["LC_ALL"] = "C", ["LANG"] = "C" };

    /// <summary>Full path of <paramref name="name"/>, or null when it is not installed. A test directory overrides the lookup.</summary>
    internal static string? Find(string name, string? binDirectory = null)
    {
        if (binDirectory is not null) return Path.Combine(binDirectory, name);
        foreach (var dir in Dirs)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;

    /// <summary>Output lines split on newlines, with blank lines removed.</summary>
    internal static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);

    internal static async Task<ProviderAvailabilityProbe> ProbeAsync(IProcessRunner runner, string? exe, string notInstalled, string versionArg, CancellationToken ct)
    {
        if (exe is null) return new(null, notInstalled);
        try
        {
            var r = await runner.RunAsync(new ProcessSpec(exe, [versionArg], TimeSpan.FromSeconds(30), Environment: CLocale), ct).ConfigureAwait(false);
            if (r.ExitCode != 0 || r.TimedOut) return new(null, Path.GetFileName(exe) + " did not respond.", true);
            return new(Lines(r.StdOut).FirstOrDefault() ?? "", null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(null, Path.GetFileName(exe) + " could not be started: " + ex.Message, true);
        }
    }
}

/// <summary>Result of starting a tool with its version flag: a version string, or why it is missing/broken.</summary>
internal readonly record struct ProviderAvailabilityProbe(string? Version, string? Problem, bool Broken = false)
{
    internal UpdateForge.Core.Models.ProviderAvailability ToAvailability() =>
        Problem is null ? new(UpdateForge.Core.Models.ProviderHealth.Available, Version, null)
        : Broken ? UpdateForge.Core.Models.ProviderAvailability.Unavailable(Problem)
        : UpdateForge.Core.Models.ProviderAvailability.NotInstalled(Problem);
}
