using Microsoft.Extensions.Logging;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;

namespace UpdateForge.Application;

public sealed record PackageInstallOutcome(PackageSearchResult Package, JobState State, string Message)
{
    public bool Installed => State is JobState.Completed or JobState.RestartRequired;
    public bool RestartRequired => State == JobState.RestartRequired;
}

/// <summary>
/// Installs one package chosen from Discover. The package identity came from a manager's search output, so the provider
/// validates it again before anything reaches a command line. Runs under the engine lock so an install can never overlap an
/// update run, and every attempt is recorded in history.
/// </summary>
public sealed class PackageInstallService(ProviderRegistry registry, ISettingsStore settingsStore, IHistoryStore history,
    IEngineLock engineLock, ILogger<PackageInstallService> log)
{
    public async Task<PackageInstallOutcome> InstallAsync(PackageSearchResult package, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var provider = registry.Find(package.ProviderId);
        if (provider is null) return new(package, JobState.Blocked, "Unknown package manager.");

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.EnabledProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase))
            return new(package, JobState.Blocked, $"{provider.DisplayName} is not enabled. Enable it in Package managers first.");
        if (!provider.Capabilities.CanUpdate)
            return new(package, JobState.ManualActionRequired, $"{provider.DisplayName} cannot install packages from UpdateForge.");

        using var held = engineLock.TryAcquire() ?? throw new EngineBusyException();
        var availability = await provider.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable) return new(package, JobState.Blocked, availability.Message ?? $"{provider.DisplayName} is unavailable.");

        progress?.Report($"Installing {package.Name} with {provider.DisplayName}…");
        var started = DateTimeOffset.UtcNow;
        UpdateResult result;
        try
        {
            var request = new InstallRequest(package.PackageId, package.Source);
            result = await provider.InstallAsync(request, new Progress<UpdateProgress>(p => progress?.Report(p.Message)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new UpdateResult { Outcome = UpdateOutcome.Canceled, Error = ErrorCategory.Canceled, Message = "Canceled." }; }
        catch (Exception ex)
        {
            log.LogError(ex, "Provider threw while installing {Package}", package.PackageId);
            result = UpdateResult.Fail(ErrorCategory.Unknown, "Unexpected provider error: " + ex.Message);
        }

        var state = StateFor(result.Outcome);
        var message = result.Message ?? state.ToString();
        await history.AddAsync(new HistoryRecord(0, DateTimeOffset.UtcNow, $"{provider.Id}:{package.PackageId}", package.Name, provider.Id, null,
            null, null, state, DateTimeOffset.UtcNow - started, result.ExitCode, result.Error,
            result.RestartRequired || state == JobState.RestartRequired, "From Discover: " + message, result.TechnicalDetails), CancellationToken.None).ConfigureAwait(false);
        return new(package, state, message);
    }

    internal static JobState StateFor(UpdateOutcome outcome) => outcome switch
    {
        UpdateOutcome.Succeeded => JobState.Completed,
        UpdateOutcome.RestartRequired => JobState.RestartRequired,
        UpdateOutcome.NoChange => JobState.Skipped,
        UpdateOutcome.Blocked => JobState.Blocked,
        UpdateOutcome.Canceled => JobState.Canceled,
        UpdateOutcome.ManualActionRequired => JobState.ManualActionRequired,
        _ => JobState.Failed,
    };
}
