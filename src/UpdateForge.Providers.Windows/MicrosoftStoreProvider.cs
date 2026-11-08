using System.Runtime.Versioning;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace UpdateForge.Providers.Windows;

/// <summary>
/// Lists Store-signed MSIX/AppX packages for the current user through the supported PackageManager API.
/// There is no supported API for third-party apps to detect or trigger updates of other Store apps, so every entry is
/// reported as "manual update required" with an Open Microsoft Store handoff. Updates are never faked.
/// Store packages that WinGet's msstore source also knows are updated through the WinGet provider instead.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MicrosoftStoreProvider : IUpdateProvider
{
    public const string ProviderKey = "msix";

    public string Id => ProviderKey;
    public string DisplayName => "Microsoft Store / MSIX (inventory)";
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: false, CanUpdate: false, SupportsPins: false);

    public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderAvailability(ProviderHealth.Available, null, null));

    public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<InstalledApplication>>(() => Scan(cancellationToken), cancellationToken);

    private static List<InstalledApplication> Scan(CancellationToken ct)
    {
        var apps = new List<InstalledApplication>();
        var now = DateTimeOffset.UtcNow;
        var pm = new PackageManager();
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (p.IsFramework || p.IsResourcePackage || p.IsBundle || p.IsOptional) continue;
                if (p.SignatureKind == PackageSignatureKind.System) continue; // inbox components, serviced by Windows Update
                var name = p.DisplayName;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;

                var v = p.Id.Version;
                apps.Add(new InstalledApplication
                {
                    Id = $"{ProviderKey}:{p.Id.FullName}",
                    Name = name,
                    ProviderId = ProviderKey,
                    PackageId = p.Id.FamilyName,
                    PackageSource = p.SignatureKind == PackageSignatureKind.Store ? "msstore" : p.SignatureKind.ToString(),
                    Publisher = string.IsNullOrWhiteSpace(p.PublisherDisplayName) ? null : p.PublisherDisplayName,
                    InstalledVersion = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    Architecture = p.Id.Architecture.ToString().ToLowerInvariant(),
                    Scope = InstallScope.User,
                    Status = UpdateStatus.Unknown,
                    Support = p.SignatureKind == PackageSignatureKind.Store ? UpdateSupport.ManualRequired : UpdateSupport.Unsupported,
                    Note = p.SignatureKind == PackageSignatureKind.Store
                        ? "Microsoft Store app. Windows does not let other apps check or trigger Store updates; use Open Microsoft Store."
                        : "Sideloaded or developer package; no supported update mechanism.",
                    LastChecked = now,
                    Evidence = [new SourceEvidence(ProviderKey, p.Id.FullName)],
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Package state changed or is not readable while scanning; skip it.
            }
        }
        return apps;
    }

    public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateResult
        {
            Outcome = UpdateOutcome.ManualActionRequired,
            Error = ErrorCategory.UserInteractionRequired,
            Message = "Open the Microsoft Store to update this app. UpdateForge cannot trigger Store updates for other apps.",
        });
}
