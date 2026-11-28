namespace UpdateForge.Application;

/// <param name="Executable">The program the schedule runs, when the platform can tell. Lets the app notice a schedule that points at a moved or removed copy.</param>
public sealed record ScheduleStatus(bool Exists, bool Enabled, DateTime? NextRun, DateTime? LastRun, string? Detail, string? Executable = null);

/// <summary>Registers the user-context scheduled task. Never creates SYSTEM or elevated tasks.</summary>
public interface IScheduleService
{
    ScheduleStatus GetStatus();
    /// <summary>Creates, updates or removes the task so it matches <paramref name="settings"/>.</summary>
    void Apply(AppSettings settings, string executablePath);
    void Remove();
}

/// <summary>Summary of a background run, persisted so the tray app can notify the user.</summary>
public sealed record BackgroundRunSummary(
    DateTimeOffset CompletedAt, int Installed, int UpdatesFound, int Updated, int Failed, int NeedsApproval, int RestartRequired,
    string? SkippedReason);

/// <summary>Tells the unattended runner whether the current connection is metered (mobile data, capped plan).</summary>
public interface INetworkCostProbe
{
    /// <summary>False when it cannot be determined: an unknown connection never blocks an update check.</summary>
    Task<bool> IsMeteredAsync(CancellationToken cancellationToken);
}
