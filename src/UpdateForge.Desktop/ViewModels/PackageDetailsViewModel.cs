using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateForge.Core.Models;

namespace UpdateForge.Desktop.ViewModels;

/// <summary>Shows what the search row already knows at once, then loads the manager's full catalog entry.</summary>
public sealed partial class PackageDetailsViewModel(DiscoveredPackageRow row,
    Func<PackageSearchResult, CancellationToken, Task<PackageDetails?>> load, Func<bool> canInstall) : ObservableObject
{
    public DiscoveredPackageRow Row { get; } = row;
    public string Title => Row.Name;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHomepage))] private Uri? _homepage;
    public bool HasHomepage => Homepage is not null;
    [ObservableProperty] private IReadOnlyList<PackageDetailField> _fields = [];

    /// <summary>Set by the window: closes it and starts the normal Discover install flow for this row.</summary>
    public Action? InstallRequested { get; set; }

    public bool ShowInstall => Row.CanInstall && canInstall();

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Message = $"Reading details from {Row.Manager}…";
        try
        {
            var details = await Task.Run(() => load(Row.Package, ct), ct);
            if (details is null) { Message = $"{Row.Manager} does not provide more details for packages."; return; }
            Description = details.Description;
            Homepage = details.Homepage;
            Fields = details.Fields;
            Message = details.Description is null && details.Fields.Count == 0 ? $"{Row.Manager} returned no further details for this package." : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Message = "Details could not be loaded: " + ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void OpenHomepage()
    {
        // PackageDetails only ever yields http(s) URIs, so this cannot launch a local program.
        if (Homepage is not { } uri) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open the homepage: " + ex.Message; }
    }

    [RelayCommand]
    private void Install() => InstallRequested?.Invoke();
}
