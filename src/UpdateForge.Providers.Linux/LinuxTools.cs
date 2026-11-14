using System.Text.Json;
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

    /// <summary>
    /// Per-user tools (cargo, pnpm, bun, yarn, pipx, Homebrew) live in the user's home or under /home/linuxbrew, usually in directories a
    /// desktop launcher's PATH lacks. Looks in the PATH, the tools' own environment variables, then their usual install folders.
    /// Relative entries are ignored so the working directory can never supply the executable.
    /// </summary>
    internal static string? FindUser(string name, string? binDirectory = null)
    {
        if (binDirectory is not null) return Path.Combine(binDirectory, name);
        foreach (var dir in UserDirs())
        {
            if (!Path.IsPathRooted(dir)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> UserDirs()
    {
        static string? Var(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Var("CARGO_HOME") is { } cargo) yield return Path.Combine(cargo, "bin");
        if (Var("BUN_INSTALL") is { } bun) yield return Path.Combine(bun, "bin");
        if (Var("PNPM_HOME") is { } pnpm) yield return pnpm;
        foreach (var d in (Var("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return d;
        foreach (var rel in (string[])[".cargo/bin", ".bun/bin", ".local/share/pnpm", ".yarn/bin", ".local/bin", ".npm-global/bin", ".linuxbrew/bin"])
            yield return Path.Combine(home, rel);
        yield return "/home/linuxbrew/.linuxbrew/bin";
        foreach (var d in Dirs) yield return d;
    }

    /// <summary>The stable-locale environment plus extra variables.</summary>
    internal static IReadOnlyDictionary<string, string> WithEnv(params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string>(CLocale);
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    /// <summary>Locates the JSON document in output that may start with notices or warnings. Null when there is none or it is malformed.</summary>
    internal static JsonDocument? ParseJson(string text, char open)
    {
        var start = text.IndexOf(open);
        if (start < 0) return null;
        try { return JsonDocument.Parse(text[start..]); }
        catch (JsonException) { return null; }
    }

    /// <summary>Runs <paramref name="work"/> over every item, at most <paramref name="limit"/> at a time, preserving order.</summary>
    internal static async Task<TResult[]> SelectLimitedAsync<T, TResult>(IEnumerable<T> items, int limit, Func<T, Task<TResult>> work, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(limit);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await work(item).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }

    internal static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;

    /// <summary>Output lines split on newlines, with blank lines removed.</summary>
    internal static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);

    internal static async Task<ProviderAvailabilityProbe> ProbeAsync(IProcessRunner runner, string? exe, string notInstalled, string versionArg, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (exe is null) return new(null, notInstalled);
        try
        {
            var r = await runner.RunAsync(new ProcessSpec(exe, [versionArg], TimeSpan.FromSeconds(30), Environment: env ?? CLocale), ct).ConfigureAwait(false);
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
