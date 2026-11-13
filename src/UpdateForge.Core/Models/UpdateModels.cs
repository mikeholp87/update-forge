namespace UpdateForge.Core.Models;

public sealed record ProviderAvailability(ProviderHealth Health, string? Version, string? Message)
{
    public bool IsAvailable => Health is ProviderHealth.Available or ProviderHealth.Degraded;
    public static ProviderAvailability Unavailable(string message) => new(ProviderHealth.Unavailable, null, message);
    public static ProviderAvailability NotInstalled(string message) => new(ProviderHealth.NotInstalled, null, message);
}

public sealed record AvailableUpdate(string ApplicationId, string PackageId, string ProviderId, string InstalledVersion, string AvailableVersion, string? Source);

/// <summary>A request to update one application. The provider re-validates every field.</summary>
public sealed record UpdateRequest(InstalledApplication Application, string? TargetVersion = null)
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
}

/// <summary>
/// A request to install a package that is not installed yet (bundle import and Discover). The provider re-validates every field.
/// <paramref name="Source"/> is the WinGet source or Scoop bucket; managers without sources (Chocolatey) ignore it.
/// </summary>
public sealed record InstallRequest(string PackageId, string? Source, string? Version = null)
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
}

public sealed record UpdateProgress(string Message, double? Fraction = null, string Phase = "Installing");

public sealed record UpdateResult
{
    public required UpdateOutcome Outcome { get; init; }
    public ErrorCategory Error { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
    public string? TechnicalDetails { get; init; }
    public bool RestartRequired { get; init; }
    public string? VersionAfter { get; init; }

    public static UpdateResult Fail(ErrorCategory error, string message, int? exitCode = null, string? details = null) =>
        new() { Outcome = UpdateOutcome.Failed, Error = error, Message = message, ExitCode = exitCode, TechnicalDetails = details };
}

public sealed record ProviderCapabilities(bool CanDiscover, bool CanDetectUpdates, bool CanUpdate, bool SupportsPins, bool SupportsParallelDownload = false);
