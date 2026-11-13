using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateForge.Application;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class PackageManagerRow(PackageManagerStatus status) : ObservableObject
{
    public PackageManagerStatus Status { get; } = status;
    public string Id => Status.Id;
    public string DisplayName => Status.DisplayName;
    public string Health => Status.Availability.Health.ToString();
    public string Version => Status.Availability.Version ?? "Not reported";
    public string Details => Status.Availability.Message ?? (Status.SupportsSources
        ? "Ready to manage packages." : "Windows inventory provider; no package manager CLI or sources.");
    public string Path => Status.ExecutablePath ?? "Built into Windows";
    public string Capabilities => string.Join(" · ", new[]
    {
        Status.Capabilities.CanDiscover ? "Inventory" : null,
        Status.Capabilities.CanDetectUpdates ? "Check updates" : null,
        Status.Capabilities.CanUpdate ? "Install updates" : null,
        Status.Capabilities.SupportsPins ? "Pins / holds" : null,
    }.OfType<string>());
    [ObservableProperty] private bool _isEnabled = status.Enabled;
}

public sealed partial class PackageManagersViewModel(PackageManagerService service) : ObservableObject
{
    public ObservableCollection<PackageManagerRow> Managers { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ShowSourcesCommand)), NotifyCanExecuteChangedFor(nameof(RefreshSourcesCommand)), NotifyCanExecuteChangedFor(nameof(OpenSetupCommand))]
    private PackageManagerRow? _selected;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand)), NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyCanExecuteChangedFor(nameof(ShowSourcesCommand)), NotifyCanExecuteChangedFor(nameof(RefreshSourcesCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _message = "Check manager status to get started.";
    [ObservableProperty] private string _sourceOutput = "Select a manager and choose Show sources.";
    public Func<Task>? SettingsChanged { get; set; }

    private bool CanRun() => !IsBusy;
    private bool CanSave() => !IsBusy && Managers.Count > 0;
    private bool CanShowSources() => !IsBusy && Selected is { IsEnabled: true, Status.SupportsSources: true, Status.Availability.IsAvailable: true };
    private bool CanRefreshSources() => CanShowSources() && Selected?.Status.CanRefreshSources == true;
    private bool CanOpenSetup() => Selected?.Id is "winget" or "scoop" or "chocolatey" or "npm";

    partial void OnSelectedChanged(PackageManagerRow? value) => SourceOutput = "Select Show sources to load the current configuration.";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var selectedId = Selected?.Id;
            var enabled = Managers.ToDictionary(m => m.Id, m => m.IsEnabled, StringComparer.OrdinalIgnoreCase);
            var statuses = await service.GetStatusAsync(default);
            Managers.Clear();
            foreach (var status in statuses)
            {
                var row = new PackageManagerRow(status);
                if (enabled.TryGetValue(row.Id, out var wasEnabled)) row.IsEnabled = wasEnabled;
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(PackageManagerRow.IsEnabled))
                    {
                        ShowSourcesCommand.NotifyCanExecuteChanged();
                        RefreshSourcesCommand.NotifyCanExecuteChanged();
                    }
                };
                Managers.Add(row);
            }
            Selected = Managers.FirstOrDefault(m => m.Id == selectedId) ?? Managers.FirstOrDefault();
            Message = "Manager status checked. Save changes after enabling or disabling providers.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await RunAsync(async () =>
    {
        await service.SaveEnabledAsync(Managers.Where(m => m.IsEnabled).Select(m => m.Id), default);
        if (SettingsChanged is not null) await SettingsChanged();
        Message = "Saved. The next scan and scheduled run will use these providers.";
    });

    [RelayCommand(CanExecute = nameof(CanShowSources))]
    private async Task ShowSourcesAsync()
    {
        var id = Selected!.Id;
        await RunAsync(async () => { SourceOutput = await service.GetSourcesAsync(id, default); Message = "Sources loaded."; });
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSources))]
    private async Task RefreshSourcesAsync()
    {
        var id = Selected!.Id;
        await RunAsync(async () => { SourceOutput = await service.RefreshSourcesAsync(id, default); Message = "Sources refreshed. Scan applications to check for updates."; });
    }

    [RelayCommand(CanExecute = nameof(CanOpenSetup))]
    private void OpenSetup()
    {
        var url = Selected?.Id switch
        {
            "winget" => "https://learn.microsoft.com/windows/package-manager/winget/",
            "scoop" => "https://scoop.sh/",
            "chocolatey" => "https://chocolatey.org/install",
            "npm" => "https://nodejs.org/en/download",
            _ => null,
        };
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open setup instructions: " + ex.Message; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Message = "Working…";
        try { await action(); }
        catch (Exception ex) { Message = ex.Message; SourceOutput = "Operation failed: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
