using CommunityToolkit.Mvvm.ComponentModel;
using UpdateForge.Core.Models;

namespace UpdateForge.Desktop.ViewModels;

public enum AppFilter { All, UpdatesAvailable, UpToDate, Unsupported, Excluded, Failed, RequiresAttention }

/// <summary>Presentation wrapper over an immutable inventory record.</summary>
public sealed partial class AppRowViewModel(InstalledApplication app, bool failed = false) : ObservableObject
{
    public InstalledApplication App { get; } = app;
    public bool Failed { get; } = failed;

    [ObservableProperty]
    private bool _isChecked;

    public string Name => App.Name;
    public string Publisher => App.Publisher ?? "—";
    public string Installed => App.InstalledVersion ?? "Unknown";
    public string Available => App.UpdateAvailable ? App.AvailableVersion ?? "" : "";
    public string Source => App.PackageSource is { Length: > 0 } s ? $"{App.ProviderId} · {s}" : App.ProviderId;
    public string Scope => App.Scope == InstallScope.Unknown ? "Unknown" : App.Scope.ToString();
    public string Confidence => App.Confidence.ToString();
    public string Note => App.Note ?? "";
    public string LastChecked => App.LastChecked is { } d ? d.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "—";
    public bool IsExcluded => App.IsExcluded;
    public bool CanUpdate => App.CanAutoUpdate;
    public bool IsStorePackage => string.Equals(App.PackageSource, "msstore", StringComparison.OrdinalIgnoreCase);

    public string StatusText => this switch
    {
        { Failed: true } => "Failed",
        { App.IsExcluded: true } => "Excluded",
        { App: { UpdateAvailable: true, IsPinned: true } } => "Pinned",
        { App.CanAutoUpdate: true } => "Update ready",
        { App: { UpdateAvailable: true, Support: UpdateSupport.ApprovalRequired } } => "Approval required",
        { App.UpdateAvailable: true } => "Manual update",
        { App.Status: UpdateStatus.UpToDate } => "Up to date",
        { App.Support: UpdateSupport.Unsupported } => "No update method",
        _ => "Unknown",
    };

    public bool NeedsAttention => !App.IsExcluded && (Failed || (App.UpdateAvailable && !App.CanAutoUpdate) || (App.Status == UpdateStatus.Unknown && App.Support == UpdateSupport.ManualRequired));

    public bool Matches(AppFilter filter) => filter switch
    {
        AppFilter.UpdatesAvailable => App.UpdateAvailable && !App.IsExcluded,
        AppFilter.UpToDate => App.Status == UpdateStatus.UpToDate,
        AppFilter.Unsupported => App.Support == UpdateSupport.Unsupported,
        AppFilter.Excluded => App.IsExcluded,
        AppFilter.Failed => Failed,
        AppFilter.RequiresAttention => NeedsAttention,
        _ => true,
    };
}

public sealed partial class QueueItemViewModel(string applicationId, string name) : ObservableObject
{
    public string ApplicationId { get; } = applicationId;
    public string Name { get; } = name;

    [ObservableProperty] private string _state = "Queued";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isFinished;
}
