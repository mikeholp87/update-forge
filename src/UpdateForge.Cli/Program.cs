using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;
using UpdateForge.Infrastructure;
using UpdateForge.Providers.Windows;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using UpdateForge.Providers.WinGet;

namespace UpdateForge.Cli;

internal static class ExitCodes
{
    public const int Ok = 0, Error = 1, Usage = 2, SomeFailed = 3, Busy = 4, UpdatesAvailable = 10;
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static async Task<int> Main(string[] args)
    {
        var json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToArray();
        if (rest.Length == 0 || rest[0] is "-h" or "--help" or "help") { PrintHelp(); return rest.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var services = new ServiceCollection()
            .AddUpdateForgeCore()
            .AddSingleton<IUpdateProvider, RegistryInventoryProvider>()
            .AddSingleton<IUpdateProvider, WinGetProvider>()
            .AddSingleton<IUpdateProvider, MicrosoftStoreProvider>()
            .AddSingleton<IUpdateProvider, ScoopProvider>()
            .AddSingleton<IUpdateProvider, ChocolateyProvider>()
            .AddSingleton<IScheduleService, ScheduledTaskService>()
            .AddSingleton<BackgroundRunner>()
            .BuildServiceProvider();

        try
        {
            return rest[0] switch
            {
                "scan" => await Scan(services, json, outdatedOnly: false, cts.Token),
                "list" => await List(services, json, cts.Token),
                "outdated" => await Scan(services, json, outdatedOnly: true, cts.Token),
                "update" => await Update(services, rest[1..], json, cts.Token),
                "history" => await History(services, json, cts.Token),
                "providers" => await Providers(services, json, cts.Token),
                "doctor" => await Doctor(services, json, cts.Token),
                "background" when rest.Length > 1 && rest[1] == "run" => await Background(services, json, cts.Token),
                "schedule" => await Schedule(services, rest[1..], json, cts.Token),
                "diagnostics" when rest.Length > 1 && rest[1] == "export" => await ExportDiagnostics(services, rest.Length > 2 ? rest[2] : null, cts.Token),
                _ => Fail($"Unknown command '{rest[0]}'.", ExitCodes.Usage),
            };
        }
        catch (EngineBusyException e) { return Fail(e.Message, ExitCodes.Busy); }
        catch (OperationCanceledException) { return Fail("Canceled.", ExitCodes.Error); }
    }

    private static int Fail(string message, int code) { Console.Error.WriteLine(message); return code; }

    private static void PrintHelp() => Console.WriteLine("""
        UpdateForge CLI

          updateforge scan                  Scan installed software and available updates
          updateforge list                  List the last scanned inventory (no rescan)
          updateforge outdated              Scan and show only applications with updates
          updateforge update --all          Update everything the policy allows
          updateforge update --id <id>      Update one application by its UpdateForge id or package id
          updateforge history               Show update history
          updateforge providers             Show provider availability
          updateforge doctor                Check the environment

        Options: --json   machine-readable output
        Exit codes: 0 ok, 1 error, 2 usage, 3 some updates failed, 4 another run in progress, 10 updates available (outdated)
        """);

    private static async Task<int> Scan(ServiceProvider sp, bool json, bool outdatedOnly, CancellationToken ct)
    {
        var settings = await sp.GetRequiredService<ISettingsStore>().LoadAsync(ct);
        var result = await sp.GetRequiredService<InventoryService>().ScanAsync(settings, new Progress<string>(m => { if (!json) Console.Error.WriteLine(m); }), ct);
        var apps = outdatedOnly ? result.Applications.Where(a => a.UpdateAvailable).ToList() : result.Applications.ToList();
        Print(apps, json, $"{result.Applications.Count} applications, {result.Applications.Count(a => a.UpdateAvailable)} with updates, scanned in {result.Duration.TotalSeconds:F1}s");
        foreach (var p in result.Providers.Where(p => p.Availability.Health != ProviderHealth.NotInstalled && (!p.Availability.IsAvailable || p.Error is not null)))
            Console.Error.WriteLine($"! Provider {p.ProviderId}: {p.Error ?? p.Availability.Message}");
        return outdatedOnly && apps.Count > 0 ? ExitCodes.UpdatesAvailable : ExitCodes.Ok;
    }

    private static async Task<int> List(ServiceProvider sp, bool json, CancellationToken ct)
    {
        var apps = await sp.GetRequiredService<IInventoryStore>().LoadSnapshotAsync(ct);
        Print(apps, json, $"{apps.Count} applications (cached snapshot)");
        return ExitCodes.Ok;
    }

    private static void Print(IReadOnlyList<InstalledApplication> apps, bool json, string footer)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(apps.Select(a => new
            {
                a.Id, a.Name, a.Publisher, Provider = a.ProviderId, a.PackageId, a.PackageSource, Installed = a.InstalledVersion,
                Available = a.AvailableVersion, Status = a.Status.ToString(), Support = a.Support.ToString(), Confidence = a.Confidence.ToString(),
                a.IsPinned, a.IsExcluded, a.Note,
            }), JsonOut));
            return;
        }
        Console.WriteLine($"{"Name",-42} {"Installed",-16} {"Available",-16} {"Provider",-9} Status");
        foreach (var a in apps)
            Console.WriteLine($"{Trim(a.Name, 42),-42} {Trim(a.InstalledVersion ?? "?", 16),-16} {Trim(a.AvailableVersion ?? "", 16),-16} {a.ProviderId,-9} {a.Status}/{a.Support}{(a.IsPinned ? " (pinned)" : "")}");
        Console.WriteLine(footer);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static async Task<int> Update(ServiceProvider sp, string[] args, bool json, CancellationToken ct)
    {
        var all = args.Contains("--all");
        var idIdx = Array.IndexOf(args, "--id");
        var id = idIdx >= 0 && idIdx + 1 < args.Length ? args[idIdx + 1] : null;
        if (all == (id is not null)) return Fail("Specify exactly one of --all or --id <id>.", ExitCodes.Usage);

        var settingsStore = sp.GetRequiredService<ISettingsStore>();
        var exclusions = sp.GetRequiredService<IExclusionStore>();
        var settings = await settingsStore.LoadAsync(ct);
        var scan = await sp.GetRequiredService<InventoryService>().ScanAsync(settings, null, ct);

        var targets = all
            ? scan.Applications.Where(a => a.UpdateAvailable).ToList()
            : scan.Applications.Where(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || string.Equals(a.PackageId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!all && targets.Count != 1) return Fail(targets.Count == 0 ? $"No installed application matches '{id}'." : $"'{id}' is ambiguous ({targets.Count} matches); use the full UpdateForge id.", ExitCodes.Usage);

        var policy = new UpdatePolicySettings
        {
            Mode = settings.Mode,
            ExcludedApplicationIds = await exclusions.GetExcludedAsync(ct),
            SkippedVersions = await exclusions.GetSkippedVersionsAsync(ct),
            BlockedSources = new HashSet<string>(settings.BlockedSources, StringComparer.OrdinalIgnoreCase),
            EnabledProviders = new HashSet<string>(settings.EnabledProviders, StringComparer.OrdinalIgnoreCase),
        };

        var progress = new Progress<EngineProgress>(p => { if (!json) Console.Error.WriteLine($"[{p.Index}/{p.Total}] {p.ApplicationName}: {p.State} - {p.Message}"); });
        var results = await sp.GetRequiredService<UpdateEngine>().RunAsync(targets, policy, unattended: false, progress, ct, ct);

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(results.Select(r => new { r.Application.Id, r.Application.Name, State = r.FinalState.ToString(), r.Message, ExitCode = r.Result?.ExitCode, VersionAfter = r.Result?.VersionAfter }), JsonOut));
        else
            foreach (var r in results) Console.WriteLine($"{r.Application.Name}: {r.FinalState} - {r.Message}");

        return results.Any(r => r.FinalState is JobState.Failed or JobState.Blocked) ? ExitCodes.SomeFailed : ExitCodes.Ok;
    }

    private static async Task<int> Background(ServiceProvider sp, bool json, CancellationToken ct)
    {
        var summary = await sp.GetRequiredService<BackgroundRunner>().RunAsync(ct);
        Console.WriteLine(json ? JsonSerializer.Serialize(summary, JsonOut) : summary.ToString());
        return summary.Failed > 0 ? ExitCodes.SomeFailed : ExitCodes.Ok;
    }

    private static async Task<int> Schedule(ServiceProvider sp, string[] args, bool json, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<IScheduleService>();
        var sub = args.Length > 0 ? args[0] : "status";
        switch (sub)
        {
            case "status":
                var st = svc.GetStatus();
                Console.WriteLine(json ? JsonSerializer.Serialize(st, JsonOut) : st.ToString());
                return ExitCodes.Ok;
            case "apply":
                var settings = await sp.GetRequiredService<ISettingsStore>().LoadAsync(ct);
                svc.Apply(settings, ScheduledTaskService.DefaultExecutablePath);
                Console.WriteLine("Scheduled task registered for the current user (not elevated).");
                return ExitCodes.Ok;
            case "remove":
                svc.Remove();
                Console.WriteLine("Scheduled task removed.");
                return ExitCodes.Ok;
            default:
                return Fail("Usage: updateforge schedule [status|apply|remove]", ExitCodes.Usage);
        }
    }

    private static async Task<int> ExportDiagnostics(ServiceProvider sp, string? path, CancellationToken ct)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"updateforge-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        await sp.GetRequiredService<UpdateForge.Infrastructure.Diagnostics.DiagnosticsExporter>().ExportAsync(path, ServiceCollectionExtensions.LogDirectory, ct);
        Console.WriteLine($"Redacted diagnostics written to {path}. Review it before attaching it to a GitHub issue.");
        return ExitCodes.Ok;
    }

    private static async Task<int> History(ServiceProvider sp, bool json, CancellationToken ct)
    {
        var items = await sp.GetRequiredService<IHistoryStore>().QueryAsync(null, 50, ct);
        if (json) Console.WriteLine(JsonSerializer.Serialize(items, JsonOut));
        else
        {
            foreach (var h in items) Console.WriteLine($"{h.CompletedAt.LocalDateTime:g}  {h.ApplicationName,-36} {h.PreviousVersion} -> {h.NewVersion ?? h.RequestedVersion}  {h.FinalState}  {h.Message}");
            if (items.Count == 0) Console.WriteLine("No update history yet.");
        }
        return ExitCodes.Ok;
    }

    private static async Task<int> Providers(ServiceProvider sp, bool json, CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var p in sp.GetRequiredService<ProviderRegistry>().All)
        {
            var a = await p.CheckAvailabilityAsync(ct);
            rows.Add(new { p.Id, p.DisplayName, Health = a.Health.ToString(), a.Version, a.Message, CanUpdate = p.Capabilities.CanUpdate });
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(rows, JsonOut));
        else foreach (var r in rows) Console.WriteLine(r);
        return ExitCodes.Ok;
    }

    private static async Task<int> Doctor(ServiceProvider sp, bool json, CancellationToken ct)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>
        {
            ("OS", Environment.OSVersion.Version.Build >= 19041, Environment.OSVersion.VersionString),
            ("Database", File.Exists(UpdateForge.Infrastructure.Persistence.Database.DefaultPath), UpdateForge.Infrastructure.Persistence.Database.DefaultPath),
            ("Log directory", Directory.Exists(ServiceCollectionExtensions.LogDirectory), ServiceCollectionExtensions.LogDirectory),
        };
        foreach (var p in sp.GetRequiredService<ProviderRegistry>().All)
        {
            var a = await p.CheckAvailabilityAsync(ct);
            // Optional tools that are simply not installed are informational, not failures.
            checks.Add(($"Provider {p.Id}", a.IsAvailable || a.Health == ProviderHealth.NotInstalled, a.Version ?? a.Message ?? a.Health.ToString()));
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(checks.Select(c => new { c.Name, c.Ok, c.Detail }), JsonOut));
        else foreach (var c in checks) Console.WriteLine($"[{(c.Ok ? " OK " : "FAIL")}] {c.Name}: {c.Detail}");
        return checks.All(c => c.Ok) ? ExitCodes.Ok : ExitCodes.Error;
    }
}
