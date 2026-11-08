using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Updates;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class ProviderToggle(string id, string displayName) : ObservableObject
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    [ObservableProperty] private bool _isEnabled;
}

public sealed partial class SettingsViewModel(ISettingsStore store, IScheduleService schedule, ProviderRegistry providers) : ObservableObject
{
    public IReadOnlyList<ScanInterval> Intervals { get; } = Enum.GetValues<ScanInterval>();
    // Managed automatic mode is intentionally not offered until the elevated execution mechanism is tested.
    public IReadOnlyList<AutomaticMode> Modes { get; } = [AutomaticMode.Manual, AutomaticMode.AutomaticSafe];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<ProviderToggle> ProviderToggles { get; } = [.. providers.All.OrderBy(p => p.DisplayName).Select(p => new ProviderToggle(p.Id, p.DisplayName))];

    [ObservableProperty] private AutomaticMode _mode;
    [ObservableProperty] private ScanInterval _interval;
    [ObservableProperty] private int _customHours = 24;
    [ObservableProperty] private bool _checkAtStartup = true;
    [ObservableProperty] private bool _runOnBattery;
    [ObservableProperty] private bool _runOnMetered;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _notifyNew = true;
    [ObservableProperty] private bool _notifyCompleted = true;
    [ObservableProperty] private bool _notifyFailed = true;
    [ObservableProperty] private bool _notifyRestart = true;
    [ObservableProperty] private string _scheduleText = "";
    [ObservableProperty] private string _saveMessage = "";

    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public event Action<AppSettings>? Saved;

    public async Task LoadAsync()
    {
        var s = await store.LoadAsync(default);
        Mode = s.Mode; Interval = s.Interval; CustomHours = s.CustomIntervalHours; CheckAtStartup = s.CheckAtStartup;
        RunOnBattery = s.RunOnBattery; RunOnMetered = s.RunOnMeteredConnection; CloseToTray = s.CloseToTray; MinimizeToTray = s.MinimizeToTray;
        Theme = s.Theme; NotifyNew = s.NotifyNewUpdates; NotifyCompleted = s.NotifyCompleted; NotifyFailed = s.NotifyFailed; NotifyRestart = s.NotifyRestart;
        foreach (var t in ProviderToggles) t.IsEnabled = s.EnabledProviders.Contains(t.Id, StringComparer.OrdinalIgnoreCase);
        RefreshScheduleText();
    }

    private void RefreshScheduleText()
    {
        try
        {
            var st = schedule.GetStatus();
            ScheduleText = !st.Exists ? "No scheduled task is registered." : $"Scheduled task registered. Next run: {st.NextRun?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "n/a"}";
        }
        catch (Exception ex) { ScheduleText = "Could not read the scheduled task: " + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var providersEnabled = ProviderToggles.Where(t => t.IsEnabled).Select(t => t.Id).ToList();
        var s = (await store.LoadAsync(default)) with
        {
            Mode = Mode, Interval = Interval, CustomIntervalHours = CustomHours, CheckAtStartup = CheckAtStartup,
            RunOnBattery = RunOnBattery, RunOnMeteredConnection = RunOnMetered, CloseToTray = CloseToTray, MinimizeToTray = MinimizeToTray,
            Theme = Theme, NotifyNewUpdates = NotifyNew, NotifyCompleted = NotifyCompleted, NotifyFailed = NotifyFailed, NotifyRestart = NotifyRestart,
            EnabledProviders = providersEnabled,
        };
        await store.SaveAsync(s, default);
        try
        {
            schedule.Apply(s.Validated(), ScheduledTaskService.DefaultExecutablePath);
            SaveMessage = "Saved. Scheduled task updated.";
        }
        catch (Exception ex) { SaveMessage = "Settings saved, but the scheduled task could not be updated: " + ex.Message; }
        RefreshScheduleText();
        Saved?.Invoke(s.Validated());
    }

    [RelayCommand]
    private void RemoveSchedule()
    {
        try { schedule.Remove(); SaveMessage = "Scheduled task removed."; }
        catch (Exception ex) { SaveMessage = "Could not remove the scheduled task: " + ex.Message; }
        RefreshScheduleText();
    }
}
