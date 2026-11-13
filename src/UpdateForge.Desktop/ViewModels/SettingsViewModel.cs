using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Updates;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class SettingsViewModel(ISettingsStore store, IScheduleService schedule) : ObservableObject
{
    public IReadOnlyList<ScanInterval> Intervals { get; } = Enum.GetValues<ScanInterval>();
    // Managed automatic mode is intentionally not offered until the elevated execution mechanism is tested.
    public IReadOnlyList<AutomaticMode> Modes { get; } = [AutomaticMode.Manual, AutomaticMode.AutomaticSafe];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

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
        var s = (await store.LoadAsync(default)) with
        {
            Mode = Mode, Interval = Interval, CustomIntervalHours = CustomHours, CheckAtStartup = CheckAtStartup,
            RunOnBattery = RunOnBattery, RunOnMeteredConnection = RunOnMetered, CloseToTray = CloseToTray, MinimizeToTray = MinimizeToTray,
            Theme = Theme, NotifyNewUpdates = NotifyNew, NotifyCompleted = NotifyCompleted, NotifyFailed = NotifyFailed, NotifyRestart = NotifyRestart,
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
