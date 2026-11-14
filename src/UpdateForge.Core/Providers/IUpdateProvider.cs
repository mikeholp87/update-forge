using UpdateForge.Core.Models;
using UpdateForge.Core.Versioning;

namespace UpdateForge.Core.Providers;

/// <summary>
/// A discovery/update source. Providers own their native operations; the engine owns policy, queueing,
/// persistence and verification. Providers must never execute strings supplied by remote metadata.
/// </summary>
public interface IUpdateProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }

    Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Inventory including whatever update state the provider can determine in the same pass.</summary>
    Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AvailableUpdate>> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken);

    /// <summary>Installs a package that is not installed yet. Providers that cannot do this keep the default.</summary>
    Task<UpdateResult> InstallAsync(InstallRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(UpdateResult.Fail(ErrorCategory.NoApplicableInstaller, DisplayName + " cannot install new packages."));

    /// <summary>
    /// Re-reads the installed version of one application for post-installation verification.
    /// Returns null when the provider cannot verify (the engine then reports the update as unverified).
    /// </summary>
    Task<string?> GetInstalledVersionAsync(InstalledApplication application, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>Orders two versions of this manager's packages (installed relative to candidate). Managers with their own scheme (apt) override it.</summary>
    VersionOrder CompareVersions(string? installed, string? candidate) => AppVersion.Compare(installed, candidate);
}
