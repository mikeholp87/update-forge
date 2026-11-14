using System.Text.Json;
using Microsoft.Extensions.Logging;
using UpdateForge.Application;
using UpdateForge.Core.Updates;

namespace UpdateForge.Background;

/// <summary>
/// Unattended run used by the scheduled task. Reuses the same InventoryService/UpdateEngine/UpdatePolicy as the
/// GUI and CLI. It never elevates; anything needing administrator rights or interaction is left pending.
/// </summary>
public sealed class BackgroundRunner(
    ISettingsStore settingsStore,
    IExclusionStore exclusions,
    InventoryService inventory,
    UpdateEngine engine,
    INetworkCostProbe network,
    ILogger<BackgroundRunner> log)
{
    public static string SummaryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateForge", "last-background-run.json");

    public async Task<BackgroundRunSummary> RunAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);

        if (!settings.RunOnMeteredConnection && await network.IsMeteredAsync(ct).ConfigureAwait(false))
            return await Done(new BackgroundRunSummary(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, "Metered connection"));

        var scan = await inventory.ScanAsync(settings, null, ct).ConfigureAwait(false);
        var candidates = scan.Applications.Where(a => a.UpdateAvailable).ToList();
        if (settings.Mode == AutomaticMode.Manual || settings.AutomaticPaused || candidates.Count == 0)
            return await Done(new BackgroundRunSummary(DateTimeOffset.UtcNow, scan.Applications.Count, candidates.Count, 0, 0, 0, 0,
                (settings.Mode == AutomaticMode.Manual || settings.AutomaticPaused) && candidates.Count > 0 ? (settings.AutomaticPaused ? "Automatic updates are paused" : "Automatic installation is off") : null));

        var policy = new UpdatePolicySettings
        {
            Mode = settings.Mode,
            ExcludedApplicationIds = await exclusions.GetExcludedAsync(ct).ConfigureAwait(false),
            SkippedVersions = await exclusions.GetSkippedVersionsAsync(ct).ConfigureAwait(false),
            BlockedSources = new HashSet<string>(settings.BlockedSources, StringComparer.OrdinalIgnoreCase),
            EnabledProviders = new HashSet<string>(settings.EnabledProviders, StringComparer.OrdinalIgnoreCase),
        };

        try
        {
            var results = await engine.RunAsync(candidates, policy, unattended: true, progress: null, ct).ConfigureAwait(false);
            return await Done(new BackgroundRunSummary(DateTimeOffset.UtcNow, scan.Applications.Count, candidates.Count,
                results.Count(r => r.FinalState == JobState.Completed),
                results.Count(r => r.FinalState is JobState.Failed or JobState.Blocked),
                results.Count(r => r.FinalState == JobState.ManualActionRequired),
                results.Count(r => r.FinalState == JobState.RestartRequired), null));
        }
        catch (EngineBusyException)
        {
            return await Done(new BackgroundRunSummary(DateTimeOffset.UtcNow, scan.Applications.Count, candidates.Count, 0, 0, 0, 0, "Another update run was in progress"));
        }
    }

    private async Task<BackgroundRunSummary> Done(BackgroundRunSummary s)
    {
        log.LogInformation("Background run finished: {Summary}", s);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SummaryPath)!);
            var tmp = SummaryPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(s)).ConfigureAwait(false);
            File.Move(tmp, SummaryPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not write background summary"); }
        return s;
    }
}
