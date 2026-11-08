using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;

namespace UpdateForge.Providers.Windows;

/// <summary>
/// Read-only discovery from the Windows uninstall registry keys. It never executes UninstallString or any other
/// registry value, and never uses Win32_Product / WMI (which can trigger Windows Installer repairs).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class RegistryInventoryProvider : IUpdateProvider
{
    public const string ProviderKey = "registry";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Id => ProviderKey;
    public string DisplayName => "Windows registry (inventory only)";
    public ProviderCapabilities Capabilities { get; } = new(CanDiscover: true, CanDetectUpdates: false, CanUpdate: false, SupportsPins: false);

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodePattern();

    public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderAvailability(ProviderHealth.Available, null, null));

    public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<InstalledApplication>>(() => Scan(cancellationToken), cancellationToken);

    private static List<InstalledApplication> Scan(CancellationToken ct)
    {
        var apps = new List<InstalledApplication>();
        var now = DateTimeOffset.UtcNow;
        (RegistryHive Hive, RegistryView View, string Tag, InstallScope Scope, string? Arch)[] roots =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", InstallScope.Machine, "x64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", InstallScope.Machine, "x86"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU64", InstallScope.User, null),
            (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU32", InstallScope.User, "x86"),
        ];

        foreach (var (hive, view, tag, scope, arch) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var app = Read(key, name, tag, scope, arch, now);
                    if (app is not null) apps.Add(app);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Entry removed or unreadable while scanning; skip it.
                }
            }
        }
        return apps;
    }

    private static InstalledApplication? Read(RegistryKey key, string keyName, string tag, InstallScope scope, string? arch, DateTimeOffset now)
    {
        var displayName = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(displayName)) return null;
        if (key.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (!string.IsNullOrEmpty(key.GetValue("ParentKeyName") as string)) return null; // patches / updates of other products
        if ((key.GetValue("ReleaseType") as string) is "Update" or "Hotfix" or "Security Update") return null;

        string? S(string n) => key.GetValue(n) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        var productCode = ProductCodePattern().IsMatch(keyName) ? keyName : null;
        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null;

        return new InstalledApplication
        {
            Id = $"{ProviderKey}:{tag}:{keyName}",
            Name = displayName,
            ProviderId = ProviderKey,
            Publisher = S("Publisher"),
            InstalledVersion = S("DisplayVersion"),
            InstallLocation = S("InstallLocation"),
            IconSource = S("DisplayIcon"),
            InstallDate = S("InstallDate"),
            EstimatedSizeKb = size,
            Architecture = arch,
            Scope = scope,
            ProductCode = productCode,
            Status = UpdateStatus.Unknown,
            Support = UpdateSupport.Unsupported,
            Confidence = IdentityConfidence.Exact,
            Note = "Detected from the Windows uninstall registry; no verified update mechanism.",
            LastChecked = now,
            Evidence = [new SourceEvidence(ProviderKey, $"{tag}\\{keyName}")],
        };
    }

    public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateResult
        {
            Outcome = UpdateOutcome.ManualActionRequired,
            Message = "This provider is inventory-only and never updates software.",
        });
}
