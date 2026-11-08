using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;

namespace UpdateForge.Application;

public sealed record ScanRecord(DateTimeOffset StartedAt, TimeSpan Duration, int ApplicationCount, int UpdatesAvailable, string? ProviderSummary);

public sealed record HistoryRecord(
    long Id, DateTimeOffset CompletedAt, string ApplicationId, string ApplicationName, string ProviderId,
    string? PreviousVersion, string? RequestedVersion, string? NewVersion, JobState FinalState, TimeSpan Duration,
    int? ExitCode, ErrorCategory Error, bool RestartRequired, string? Message, string? TechnicalDetails);

public sealed record JobItemRecord(long Id, string ApplicationId, string ApplicationName, string ProviderId, JobState State, DateTimeOffset UpdatedAt);

public interface IInventoryStore
{
    Task SaveSnapshotAsync(IReadOnlyList<InstalledApplication> apps, CancellationToken ct);
    Task<IReadOnlyList<InstalledApplication>> LoadSnapshotAsync(CancellationToken ct);
}

public interface IScanHistoryStore
{
    Task AddAsync(ScanRecord record, CancellationToken ct);
    Task<ScanRecord?> GetLastAsync(CancellationToken ct);
}

public interface IExclusionStore
{
    Task<IReadOnlySet<string>> GetExcludedAsync(CancellationToken ct);
    Task SetExcludedAsync(string applicationId, string applicationName, bool excluded, CancellationToken ct);
    Task<IReadOnlyList<SkippedVersion>> GetSkippedVersionsAsync(CancellationToken ct);
    Task AddSkippedVersionAsync(string applicationId, string version, CancellationToken ct);
    Task RemoveSkippedVersionAsync(string applicationId, string version, CancellationToken ct);
}

public interface IJobStore
{
    Task<long> CreateItemAsync(Guid batchId, InstalledApplication app, JobState state, CancellationToken ct);
    Task SetStateAsync(long itemId, JobState state, string? message, CancellationToken ct);
    /// <summary>Marks items left in an in-flight state by a crash as Failed (never Completed) and returns them.</summary>
    Task<IReadOnlyList<JobItemRecord>> RecoverInterruptedAsync(CancellationToken ct);
    Task<IReadOnlyList<JobItemRecord>> GetRecentAsync(int limit, CancellationToken ct);
}

public interface IHistoryStore
{
    Task AddAsync(HistoryRecord record, CancellationToken ct);
    Task<IReadOnlyList<HistoryRecord>> QueryAsync(string? search, int limit, CancellationToken ct);
    Task<DateTimeOffset?> GetLastSuccessAsync(CancellationToken ct);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}

/// <summary>Prevents two engines (GUI, CLI, scheduled task) from modifying the queue at once.</summary>
public interface IEngineLock
{
    /// <summary>Returns null if another engine already holds the lock.</summary>
    IDisposable? TryAcquire();
}
