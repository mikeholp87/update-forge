using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Providers.Npm;

public sealed record NpmPackage(string Name, string Version, bool Linked);

public static class NpmParsing
{
    /// <summary>
    /// npm can print notices before the JSON document and exits non-zero for harmless reasons (for example
    /// <c>npm outdated</c> exits 1 when something is outdated), so the JSON is located rather than assumed to start at byte 0.
    /// </summary>
    internal static JsonDocument? ParseJson(string text, char open)
    {
        var start = text.IndexOf(open);
        if (start < 0) return null;
        try { return JsonDocument.Parse(text[start..]); }
        catch (JsonException) { return null; }
    }

    /// <summary>Parses <c>npm ls -g --depth=0 --json</c>. Entries without a version (missing, invalid) are skipped.</summary>
    public static IReadOnlyList<NpmPackage> ParseList(string json)
    {
        using var doc = ParseJson(json, '{') ?? throw new InvalidOperationException("npm did not return a package list.");
        var list = new List<NpmPackage>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("dependencies", out var deps)
            || deps.ValueKind != JsonValueKind.Object) return list;
        foreach (var d in deps.EnumerateObject())
        {
            if (d.Value.ValueKind != JsonValueKind.Object) continue;
            if (!d.Value.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) continue;
            var linked = d.Value.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.String
                && (r.GetString() ?? "").StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            list.Add(new NpmPackage(d.Name, v.GetString()!, linked));
        }
        return list;
    }

    /// <summary>Parses <c>npm outdated -g --json</c> into name → latest version. An empty document means nothing is outdated.</summary>
    public static IReadOnlyDictionary<string, string> ParseOutdated(string json)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = ParseJson(json, '{');
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return latest;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            // npm prints one object per package, but an array when a package is installed in several locations.
            var entry = p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0 ? p.Value[0] : p.Value;
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(l.GetString())) latest[p.Name] = l.GetString()!;
        }
        return latest;
    }

    /// <summary>Parses <c>npm view &lt;name&gt; --json</c>.</summary>
    public static PackageDetails ParseView(string json)
    {
        using var doc = ParseJson(json, '{') ?? throw new InvalidOperationException("npm did not return package details.");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("npm did not return package details.");
        var fields = new List<PackageDetailField>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(name, value)); }

        var version = root.TryGetProperty("dist-tags", out var tags) && tags.ValueKind == JsonValueKind.Object ? Text(tags, "latest") : null;
        version ??= Text(root, "version");
        Add("Description", Text(root, "description"));
        Add("Latest version", version);
        Add("Homepage", Text(root, "homepage"));
        Add("License", Text(root, "license"));
        Add("Author", Text(root, "author"));
        var repository = Text(root, "repository");
        Add("Repository", repository?.StartsWith("git+", StringComparison.OrdinalIgnoreCase) == true ? repository[4..] : repository);
        Add("Issues", Text(root, "bugs"));
        Add("Keywords", Text(root, "keywords"));
        if (version is not null && root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
            && Text(time, version) is { } published
            && DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            Add("Published", when.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        Add("Maintainers", Text(root, "maintainers"));
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            Add("Dependencies", deps.EnumerateObject().Count().ToString(CultureInfo.InvariantCulture));
        Add("Node.js", root.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object ? Text(engines, "node") : null);
        return PackageDetails.From(fields);
    }

    /// <summary>Reads a string, a person/repository object (name or url), or an array of either.</summary>
    private static string? Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object => ObjectText(v),
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Object ? ObjectText(x) : null)
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => null,
        };
    }

    private static string? ObjectText(JsonElement o)
    {
        foreach (var key in (string[])["name", "url", "type"])
            if (o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())) return p.GetString();
        return null;
    }

    /// <summary>Parses <c>npm search --json</c>. Names that do not look like valid package names are dropped.</summary>
    public static IReadOnlyList<PackageSearchResult> ParseSearch(string json, Func<string, bool> isValidName)
    {
        using var doc = ParseJson(json, '[');
        var list = new List<PackageSearchResult>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            var name = n.GetString()!;
            if (!isValidName(name)) continue;
            var version = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            list.Add(new PackageSearchResult("npm", name, name, version.Length == 0 ? "Unknown" : version, null));
        }
        return list;
    }
}

/// <summary>
/// npm provider for globally installed packages. npm is a Node.js script, so it is started as
/// <c>node.exe npm-cli.js …</c> with typed arguments: never through <c>npm.cmd</c> or a shell, so nothing in a package name
/// can be reinterpreted by <c>cmd.exe</c>. Package names and versions are validated before they reach the command line.
/// Linked packages (<c>npm link</c>) are reported as pinned and never replaced.
/// Not verified against every Node.js layout (nvm, Volta); if node.exe and npm-cli.js are not found the provider reports itself as not installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class NpmProvider : IUpdateProvider, IPackageManager, IPackageSearchProvider
{
    public const string ProviderKey = "npm";
    private readonly IProcessRunner _runner;
    private readonly ILogger<NpmProvider> _log;
    private readonly string _node;
    private readonly string _npmCli;
    private readonly Func<bool> _isElevated;

    public NpmProvider(IProcessRunner runner, ILogger<NpmProvider> log, string? nodeExe = null, string? npmCli = null, Func<bool>? isElevated = null)
    {
        _runner = runner;
        _log = log;
        (_node, _npmCli) = nodeExe is not null && npmCli is not null ? (nodeExe, npmCli) : Locate();
        _isElevated = isElevated ?? (() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
    }

    public string Id => ProviderKey;
    public string DisplayName => "npm";
    public string ExecutablePath => _npmCli;
    public bool CanRefreshSources => false;
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, SupportsPins: false);

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~\-]{0,213}/)?[a-z0-9][a-z0-9._~\-]{0,213}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    internal static bool IsValidName(string? name) => name is { Length: <= 214 } && NamePattern().IsMatch(name);

    /// <summary>Finds node.exe and the npm CLI script next to it (the layout of the official Windows installer).</summary>
    private static (string Node, string Cli) Locate()
    {
        var dirs = new List<string>();
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(d.Trim().Trim('"'));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"));
        foreach (var dir in dirs)
        {
            try
            {
                var node = Path.Combine(dir, "node.exe");
                var cli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(node) && File.Exists(cli)) return (node, cli);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { /* malformed PATH entry */ }
        }
        return ("", "");
    }

    private ProcessSpec Spec(TimeSpan timeout, Action<string>? onLine, params string[] args) =>
        new(_node, [_npmCli, .. args, "--no-fund", "--no-audit", "--loglevel=error", "--update-notifier=false", "--progress=false", "--color=false"], timeout, onLine);

    public async Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_node.Length == 0 || !File.Exists(_node) || !File.Exists(_npmCli)) return ProviderAvailability.NotInstalled("Node.js and npm are not installed.");
        try
        {
            var r = await _runner.RunAsync(Spec(TimeSpan.FromSeconds(60), null, "--version"), cancellationToken).ConfigureAwait(false);
            return r.ExitCode == 0 && !r.TimedOut ? new ProviderAvailability(ProviderHealth.Available, r.StdOut.Trim(), null) : ProviderAvailability.Unavailable("npm did not respond.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ProviderAvailability.Unavailable("npm could not be started: " + ex.Message);
        }
    }

    public async Task<string> GetSourcesAsync(CancellationToken cancellationToken) =>
        "Registry: " + PackageManagerOutput.RequireSuccess(await _runner.RunAsync(Spec(TimeSpan.FromSeconds(30), null,
            "config", "get", "registry"), cancellationToken).ConfigureAwait(false), "Read npm registry");

    public Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("npm queries its registry on every check; there is nothing to refresh.");

    public async Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var ls = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "ls", "-g", "--depth=0", "--json"), cancellationToken).ConfigureAwait(false);
        // A failed or timed-out query must surface as a provider problem, never as "everything is up to date".
        // npm ls exits 1 for harmless tree problems but still prints the list, so the JSON decides, not the exit code.
        if (ls.TimedOut) throw new TimeoutException("npm ls timed out.");
        var packages = NpmParsing.ParseList(ls.StdOut);

        var outdated = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(3), null, "outdated", "-g", "--json"), cancellationToken).ConfigureAwait(false);
        if (outdated.TimedOut) throw new TimeoutException("npm outdated timed out.");
        // npm outdated exits 1 when it finds outdated packages. Any other failure without a JSON document (offline, registry error) is a real failure.
        if (outdated.ExitCode > 1 || (outdated.ExitCode == 1 && !outdated.StdOut.Contains('{')))
            throw new InvalidOperationException($"npm outdated failed (exit code {outdated.ExitCode}). {outdated.StdErr.Trim()}");
        var latest = NpmParsing.ParseOutdated(outdated.StdOut);

        var now = DateTimeOffset.UtcNow;
        return [.. packages.Select(p =>
        {
            latest.TryGetValue(p.Name, out var avail);
            var newer = !p.Linked && avail is not null && AppVersion.IsNewer(p.Version, avail);
            return new InstalledApplication
            {
                Id = $"{ProviderKey}:{p.Name}",
                Name = p.Name,
                ProviderId = ProviderKey,
                PackageId = p.Name,
                PackageSource = ProviderKey,
                InstalledVersion = p.Version,
                AvailableVersion = newer ? avail : null,
                Status = newer ? UpdateStatus.UpdateAvailable : AppVersion.TryParse(p.Version, out _, out _) ? UpdateStatus.UpToDate : UpdateStatus.Unknown,
                Support = UpdateSupport.AutoUpdatable,
                Scope = InstallScope.User,
                IsPinned = p.Linked,
                Note = p.Linked ? "Linked from a local folder with npm link; UpdateForge will not replace it." : null,
                LastChecked = now,
                Evidence = [new SourceEvidence(ProviderKey, p.Name, ProviderKey)],
            };
        })];
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var app = request.Application;
        var version = request.TargetVersion ?? app.AvailableVersion;
        if (app.ProviderId != ProviderKey || !IsValidName(app.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "npm package name failed validation; refusing to run.");
        if (version is null || !VersionPattern().IsMatch(version))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "npm target version failed validation; refusing to run.");
        if (app.IsPinned) return new UpdateResult { Outcome = UpdateOutcome.Blocked, Message = "Package is linked locally with npm link." };

        // Installing the exact version that was checked, not "latest", keeps the result identical to what the user was shown.
        _log.LogInformation("Updating npm package {Package} to {Version}", app.PackageId, version);
        return await RunInstallAsync($"{app.PackageId}@{version}", "Updated with npm.", progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsValidName(request.PackageId))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "npm package name failed validation; refusing to run.");
        if (request.Version is { } v && !VersionPattern().IsMatch(v))
            return UpdateResult.Fail(ErrorCategory.SecurityBlocked, "npm version failed validation; refusing to run.");

        _log.LogInformation("Installing npm package {Package} (correlation {Correlation})", request.PackageId, request.CorrelationId);
        return await RunInstallAsync(request.Version is null ? request.PackageId : $"{request.PackageId}@{request.Version}", "Installed.", progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateResult> RunInstallAsync(string target, string successMessage, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new UpdateProgress("Starting npm…", null, "Installing"));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(30), l => { if (l.Trim().Length > 3) progress.Report(new UpdateProgress(l.Trim())); },
            "install", "-g", target), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return UpdateResult.Fail(ErrorCategory.Timeout, "npm timed out.");

        var text = (r.StdOut + "\n" + r.StdErr).Trim();
        var tail = text.Length > 4000 ? text[^4000..] : text;
        if (r.ExitCode == 0) return new UpdateResult { Outcome = UpdateOutcome.Succeeded, ExitCode = 0, Message = successMessage, TechnicalDetails = tail };

        if (text.Contains("EACCES", StringComparison.Ordinal) || text.Contains("EPERM", StringComparison.Ordinal))
        {
            return _isElevated()
                ? UpdateResult.Fail(ErrorCategory.InstallerFailed, "npm could not write to its global folder (permission denied).", r.ExitCode, tail)
                : new UpdateResult { Outcome = UpdateOutcome.ManualActionRequired, Error = ErrorCategory.ElevationRequired, ExitCode = r.ExitCode, TechnicalDetails = tail,
                    Message = "npm could not write to its global folder. Run UpdateForge as administrator, or point npm's prefix at a folder you own." };
        }
        if (text.Contains("EBUSY", StringComparison.Ordinal))
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Error = ErrorCategory.ApplicationInUse, ExitCode = r.ExitCode, TechnicalDetails = tail,
                Message = "A file is in use. Close the program that is using this package and try again." };
        if (text.Contains("ENOTFOUND", StringComparison.Ordinal) || text.Contains("ETIMEDOUT", StringComparison.Ordinal) || text.Contains("ECONNRESET", StringComparison.Ordinal))
            return UpdateResult.Fail(ErrorCategory.Network, "npm could not reach its registry.", r.ExitCode, tail);
        return UpdateResult.Fail(ErrorCategory.InstallerFailed, $"npm exited with code {r.ExitCode}.", r.ExitCode, tail);
    }

    public async Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken)
    {
        if (!IsValidName(application.PackageId)) return null;
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "ls", "-g", application.PackageId!, "--depth=0", "--json"), cancellationToken).ConfigureAwait(false);
        if (r.TimedOut) return null;
        try { return NpmParsing.ParseList(r.StdOut).FirstOrDefault(p => p.Name.Equals(application.PackageId, StringComparison.OrdinalIgnoreCase))?.Version; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken)
    {
        query = PackageSearchQuery.Validate(query);
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(2), null, "search", query, "--json", "--searchlimit=40"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "Search npm packages");
        return NpmParsing.ParseSearch(r.StdOut, IsValidName);
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
    {
        if (!IsValidName(package.PackageId)) throw new ArgumentException("npm package name failed validation.", nameof(package));
        var r = await _runner.RunAsync(Spec(TimeSpan.FromMinutes(1), null, "view", package.PackageId, "--json"), cancellationToken).ConfigureAwait(false);
        PackageManagerOutput.RequireSuccess(r, "Read npm package details");
        return NpmParsing.ParseView(r.StdOut);
    }
}
