namespace UpdateForge.Core.Updates;

public enum JobState
{
    Discovered, Checking, UpdateAvailable, Approved, Queued, Downloading, Verifying, Installing,
    VerifyingInstallation, Completed, Failed, RestartRequired, Skipped, Blocked, Canceled, ManualActionRequired,
}

/// <summary>Allowed lifecycle transitions. Terminal states have no outgoing edges except explicit retry to Queued.</summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Transitions = new()
    {
        [JobState.Discovered] = [JobState.Checking, JobState.Skipped, JobState.Blocked],
        [JobState.Checking] = [JobState.UpdateAvailable, JobState.Discovered, JobState.Failed, JobState.Skipped],
        [JobState.UpdateAvailable] = [JobState.Approved, JobState.Skipped, JobState.Blocked, JobState.ManualActionRequired],
        [JobState.Approved] = [JobState.Queued, JobState.Canceled, JobState.Blocked],
        [JobState.Queued] = [JobState.Downloading, JobState.Installing, JobState.Canceled, JobState.Blocked, JobState.Failed],
        [JobState.Downloading] = [JobState.Verifying, JobState.Installing, JobState.Failed, JobState.Canceled],
        [JobState.Verifying] = [JobState.Installing, JobState.Failed, JobState.Canceled],
        [JobState.Installing] = [JobState.VerifyingInstallation, JobState.Failed, JobState.Canceled, JobState.RestartRequired, JobState.ManualActionRequired],
        [JobState.VerifyingInstallation] = [JobState.Completed, JobState.Failed, JobState.RestartRequired],
        [JobState.Failed] = [JobState.Queued],
        [JobState.Canceled] = [JobState.Queued],
        [JobState.Blocked] = [JobState.Approved],
        [JobState.ManualActionRequired] = [JobState.Queued],
        [JobState.Skipped] = [JobState.UpdateAvailable],
        [JobState.RestartRequired] = [],
        [JobState.Completed] = [],
    };

    public static bool CanTransition(JobState from, JobState to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);

    public static JobState Transition(JobState from, JobState to) =>
        CanTransition(from, to) ? to : throw new InvalidOperationException($"Illegal job transition {from} -> {to}.");

    public static bool IsTerminal(JobState s) => s is JobState.Completed or JobState.Failed or JobState.RestartRequired
        or JobState.Canceled or JobState.Skipped or JobState.Blocked or JobState.ManualActionRequired;

    /// <summary>States that, found after a crash, mean the install may or may not have happened. Never reported as success.</summary>
    public static bool IsInFlight(JobState s) => s is JobState.Downloading or JobState.Verifying or JobState.Installing or JobState.VerifyingInstallation;
}
