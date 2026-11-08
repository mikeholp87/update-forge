namespace UpdateForge.Core.Models;

/// <summary>A piece of evidence that an application exists, from one discovery source.</summary>
public sealed record SourceEvidence(string ProviderId, string SourceKey, string? Detail = null);

/// <summary>Normalized view of an installed application, regardless of how it was discovered.</summary>
public sealed record InstalledApplication
{
    /// <summary>Stable identity key, e.g. "winget:Mozilla.Firefox" or "registry:HKLM64:{GUID}".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ProviderId { get; init; }
    /// <summary>Provider-native package identifier (WinGet id, Scoop name, Chocolatey id).</summary>
    public string? PackageId { get; init; }
    /// <summary>Catalog/source the package comes from (e.g. winget, msstore, a Scoop bucket).</summary>
    public string? PackageSource { get; init; }
    public string? Publisher { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? Architecture { get; init; }
    public InstallScope Scope { get; init; }
    public string? ProductCode { get; init; }
    public UpdateStatus Status { get; init; }
    public UpdateSupport Support { get; init; }
    public IdentityConfidence Confidence { get; init; } = IdentityConfidence.Exact;
    public bool IsExcluded { get; init; }
    public bool IsPinned { get; init; }
    public bool RequiresElevation { get; init; }
    public bool RequiresUserInteraction { get; init; }
    public bool RequiresRestart { get; init; }
    /// <summary>Raw DisplayIcon value. Used only to render an icon; never executed.</summary>
    public string? IconSource { get; init; }
    public string? InstallDate { get; init; }
    public long? EstimatedSizeKb { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset? LastChecked { get; init; }
    public IReadOnlyList<SourceEvidence> Evidence { get; init; } = [];

    public bool UpdateAvailable => Status == UpdateStatus.UpdateAvailable;
    public bool CanAutoUpdate => Support == UpdateSupport.AutoUpdatable && UpdateAvailable && !IsExcluded && !IsPinned;
}
