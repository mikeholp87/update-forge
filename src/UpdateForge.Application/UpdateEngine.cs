using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Application;

public sealed class EngineBusyException() : InvalidOperationException("Another UpdateForge update run is already in progress.");

public sealed record EngineProgress(string ApplicationId, string ApplicationName, JobState State, string Message, int Index, int Total, double? Fraction);

public sealed record UpdateItemResult(InstalledApplication Application, JobState FinalState, UpdateResult? Result, string Message);

/// <summary>
/// Sequential update engine shared by the GUI, CLI and background task. Policy is evaluated for every item,
/// state changes are persisted, and "Completed" is only reported after provider success plus verification
/// where the provider supports it.
/// </summary>
public sealed class UpdateEngine(
    ProviderRegistry registry,
    IJobStore jobs,
    IHistoryStore history,
    IEngineLock engineLock,
    ILogger<UpdateEngine> log)
{
    /// <param name="softCancel">Stops starting new updates; the running installer is allowed to finish.</param>
    /// <param name="hardCancel">Also terminates the running installer. Can leave a partial install; use sparingly.</param>
    public async Task<IReadOnlyList<UpdateItemResult>> RunAsync(
        IReadOnlyList<InstalledApplication> apps, UpdatePolicySettings policy, bool unattended,
        IProgress<EngineProgress>? progress, CancellationToken softCancel, CancellationToken hardCancel = default)
    {
        using var held = engineLock.TryAcquire() ?? throw new EngineBusyException();
        var recovered = await jobs.RecoverInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var r in recovered) log.LogWarning("Recovered interrupted job {Id} for {App}; marked Failed (outcome unknown).", r.Id, r.ApplicationName);

        var batch = Guid.NewGuid();
        var results = new List<UpdateItemResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = apps.Count;
        var index = 0;

        foreach (var app in apps)
        {
            index++;
            void Report(JobState s, string m, double? f = null) => progress?.Report(new EngineProgress(app.Id, app.Name, s, m, index, total, f));

            if (!seen.Add(app.Id)) { results.Add(new(app, JobState.Skipped, null, "Duplicate request ignored.")); continue; }
            if (softCancel.IsCancellationRequested)
            {
                results.Add(new(app, JobState.Canceled, null, "Canceled before starting."));
                Report(JobState.Canceled, "Canceled before starting.");
                continue;
            }

            var decision = UpdatePolicy.Evaluate(app, policy, unattended);
            if (!decision.IsAllowed)
            {
                var state = decision.Verdict switch
                {
                    PolicyVerdict.Skip => JobState.Skipped,
                    PolicyVerdict.NeedsApproval or PolicyVerdict.Unsupported => JobState.ManualActionRequired,
                    _ => JobState.Blocked,
                };
                results.Add(new(app, state, null, decision.Reason));
                Report(state, decision.Reason);
                continue;
            }

            var provider = registry.Find(app.ProviderId);
            if (provider is null || !provider.Capabilities.CanUpdate)
            {
                results.Add(new(app, JobState.ManualActionRequired, null, "No update provider available."));
                Report(JobState.ManualActionRequired, "No update provider available.");
                continue;
            }

            results.Add(await RunOneAsync(app, provider, batch, Report, hardCancel).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<UpdateItemResult> RunOneAsync(
        InstalledApplication app, Core.Providers.IUpdateProvider provider, Guid batch,
        Action<JobState, string, double?> report, CancellationToken hardCancel)
    {
        var sw = Stopwatch.StartNew();
        var itemId = await jobs.CreateItemAsync(batch, app, JobState.Queued, CancellationToken.None).ConfigureAwait(false);
        var state = JobState.Queued;

        async Task Move(JobState next, string message)
        {
            state = JobStateMachine.Transition(state, next);
            await jobs.SetStateAsync(itemId, state, message, CancellationToken.None).ConfigureAwait(false);
            report(state, message, null);
        }

        UpdateResult result;
        try
        {
            await Move(JobState.Installing, "Starting update…").ConfigureAwait(false);
            var sink = new Progress<UpdateProgress>(p => report(state, p.Message, p.Fraction));
            result = await provider.UpdateAsync(new UpdateRequest(app), sink, hardCancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, Message = "Canceled." };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Provider {Provider} threw while updating {App}", provider.Id, app.Name);
            result = UpdateResult.Fail(ErrorCategory.Unknown, "Unexpected provider error: " + ex.Message);
        }

        var finalState = JobState.Failed;
        string? versionAfter = null;
        var message = result.Message ?? string.Empty;

        switch (result.Outcome)
        {
            case UpdateOutcome.Succeeded:
            case UpdateOutcome.RestartRequired:
                await Move(JobState.VerifyingInstallation, "Verifying installation…").ConfigureAwait(false);
                versionAfter = await TryVerifyAsync(provider, app).ConfigureAwait(false);
                // Package managers can report the old version briefly after an install; re-check before declaring failure.
                for (var i = 0; i < 3 && result.Outcome == UpdateOutcome.Succeeded && versionAfter is not null
                     && AppVersion.Compare(app.InstalledVersion, versionAfter) != VersionOrder.Older; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                    versionAfter = await TryVerifyAsync(provider, app).ConfigureAwait(false);
                }
                if (result.Outcome == UpdateOutcome.RestartRequired) { finalState = JobState.RestartRequired; break; }
                if (versionAfter is null) { finalState = JobState.Completed; message += " (could not be independently verified)"; }
                else if (AppVersion.Compare(app.InstalledVersion, versionAfter) == VersionOrder.Older) finalState = JobState.Completed;
                else
                {
                    finalState = JobState.Failed;
                    result = UpdateResult.Fail(ErrorCategory.VerificationFailed, "The installer reported success but the installed version did not change.", result.ExitCode, result.TechnicalDetails);
                    message = result.Message!;
                }
                break;
            case UpdateOutcome.NoChange: finalState = JobState.Skipped; break;
            case UpdateOutcome.Blocked: finalState = JobState.Blocked; break;
            case UpdateOutcome.Canceled: finalState = JobState.Canceled; break;
            case UpdateOutcome.ManualActionRequired: finalState = JobState.ManualActionRequired; break;
            default: finalState = JobState.Failed; break;
        }

        // Terminal outcomes are recorded directly (e.g. Blocked straight from Installing), not forced through the state machine.
        await jobs.SetStateAsync(itemId, finalState, message, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        await history.AddAsync(new HistoryRecord(0, DateTimeOffset.UtcNow, app.Id, app.Name, app.ProviderId, app.InstalledVersion,
            app.AvailableVersion, versionAfter, finalState, sw.Elapsed, result.ExitCode, result.Error, result.RestartRequired || finalState == JobState.RestartRequired,
            message, result.TechnicalDetails), CancellationToken.None).ConfigureAwait(false);

        report(finalState, message, 1);
        return new UpdateItemResult(app, finalState, result with { VersionAfter = versionAfter }, message);
    }

    private async Task<string?> TryVerifyAsync(Core.Providers.IUpdateProvider provider, InstalledApplication app)
    {
        try { return await provider.GetInstalledVersionAsync(app, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Post-install verification failed for {App}", app.Name);
            return null;
        }
    }
}
