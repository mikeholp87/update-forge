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
        await RefreshScheduleTextAsync(s);
    }

    /// <summary>
    /// systemctl calls can take seconds, so they run on a worker thread, never the UI thread. A timer that points at a program that moved or was
    /// removed (a portable copy that was relocated, an old install) is re-registered here: it is the schedule the user already chose.
    /// </summary>
    private async Task RefreshScheduleTextAsync(AppSettings? settings = null)
    {
        try
        {
            var current = SystemdScheduleService.DefaultExecutablePath;
            var text = await Task.Run(() =>
            {
                var st = schedule.GetStatus();
                if (st.Exists && settings is not null && st.Executable is { } exe && exe != current && File.Exists(current))
                {
                    schedule.Apply(settings.Validated(), current);
                    st = schedule.GetStatus();
                    return "The systemd timer pointed at a program that moved, so it was re-registered. " + Describe(st);
                }
                return Describe(st);
            });
            ScheduleText = text;
        }
        catch (Exception ex) { ScheduleText = "Could not read the systemd timer: " + ex.Message; }
    }

    private static string Describe(ScheduleStatus st) => !st.Exists ? "No systemd timer is registered."
        : st.Executable is { } exe && !File.Exists(exe) ? $"systemd timer registered, but it runs {exe}, which no longer exists. Save the settings to fix it."
        : $"systemd timer registered. Next run: {st.NextRun?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "n/a"}";

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
            var validated = s.Validated();
            await Task.Run(() => schedule.Apply(validated, SystemdScheduleService.DefaultExecutablePath));
            SaveMessage = "Saved. systemd timer updated.";
        }
        catch (Exception ex) { SaveMessage = "Settings saved, but the systemd timer could not be updated: " + ex.Message; }
        await RefreshScheduleTextAsync();
        Saved?.Invoke(s.Validated());
    }

    [RelayCommand]
    private async Task RemoveScheduleAsync()
    {
        try { await Task.Run(schedule.Remove); SaveMessage = "systemd timer removed."; }
        catch (Exception ex) { SaveMessage = "Could not remove the systemd timer: " + ex.Message; }
        await RefreshScheduleTextAsync();
    }
}
