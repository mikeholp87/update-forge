using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Providers;
using UpdateForge.Desktop.Services;
using UpdateForge.Desktop.ViewModels;
using UpdateForge.Infrastructure;
using UpdateForge.Providers.Windows;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using UpdateForge.Providers.WinGet;
using Wpf.Ui.Appearance;

namespace UpdateForge.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\UpdateForge.Gui";
    private const string ShowEventName = @"Local\UpdateForge.Gui.Show";

    private Mutex? _single;
    private EventWaitHandle? _showEvent;
    private ServiceProvider? _services;
    private TrayService? _tray;
    private MainWindow? _window;
    private FileSystemWatcher? _summaryWatcher;
    private AppSettings _settings = new();
    private readonly object _summaryLock = new();
    private DateTimeOffset _lastSummaryAt = DateTimeOffset.UtcNow; // ignore a summary left over from before this launch

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { await StartCoreAsync(e); }
        catch (Exception ex)
        {
            // ShutdownMode is OnExplicitShutdown, so an unhandled startup failure (corrupt database, newer schema, ...)
            // would otherwise leave an invisible zombie process holding the single-instance mutex.
            System.Windows.MessageBox.Show("UpdateForge could not start: " + ex.Message, "UpdateForge", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
            _tray?.Dispose();
            Shutdown(1);
        }
    }

    private async Task StartCoreAsync(StartupEventArgs e)
    {

        _single = new Mutex(true, MutexName, out var first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            _showEvent.Set(); // ask the running instance to come to the foreground
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _services?.GetService<Microsoft.Extensions.Logging.ILogger<App>>()?.LogUnhandled(args.Exception);
            System.Windows.MessageBox.Show("UpdateForge hit an unexpected error: " + args.Exception.Message + "\n\nDetails were written to the log folder.", "UpdateForge", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _services = new ServiceCollection()
            .AddUpdateForgeCore()
            .AddSingleton<IUpdateProvider, RegistryInventoryProvider>()
            .AddSingleton<IUpdateProvider, WinGetProvider>()
            .AddSingleton<IUpdateProvider, MicrosoftStoreProvider>()
            .AddSingleton<IUpdateProvider, ScoopProvider>()
            .AddSingleton<IUpdateProvider, ChocolateyProvider>()
            .AddSingleton<IScheduleService, ScheduledTaskService>()
            .AddSingleton<HistoryViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<PackageManagersViewModel>()
            .AddSingleton<DiscoverViewModel>()
            .AddSingleton<ShellViewModel>()
            .BuildServiceProvider();

        var shell = _services.GetRequiredService<ShellViewModel>();
        var settingsStore = _services.GetRequiredService<ISettingsStore>();
        _settings = await settingsStore.LoadAsync(default);
        ApplyTheme(_settings.Theme);

        _window = new MainWindow { DataContext = shell };
        _window.ShouldHideOnClose = () => _settings.CloseToTray;
        _window.ExitRequested = ExitApplication;
        SystemThemeWatcher.Watch(_window);

        _tray = new TrayService();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowWindow);
        _tray.ScanRequested += () => Dispatcher.Invoke(() => shell.ScanCommand.Execute(null));
        _tray.UpdateRequested += () => Dispatcher.Invoke(() => { ShowWindow(); shell.UpdateAllCommand.Execute(null); });
        _tray.HistoryRequested += () => Dispatcher.Invoke(() => { ShowWindow(); shell.NavigateCommand.Execute(PageId.History); });
        _tray.SettingsRequested += () => Dispatcher.Invoke(() => { ShowWindow(); shell.NavigateCommand.Execute(PageId.Settings); });
        _tray.PauseToggled += async paused =>
        {
            try
            {
                var s = await settingsStore.LoadAsync(default);
                if (s.AutomaticPaused == paused) return;
                await settingsStore.SaveAsync(s with { AutomaticPaused = paused }, default);
                _settings = _settings with { AutomaticPaused = paused };
            }
            catch (Exception ex)
            {
                // async void handler: an exception here would take the whole process down.
                _services?.GetService<Microsoft.Extensions.Logging.ILogger<App>>()?.LogUnhandled(ex);
            }
        };
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApplication);
        _tray.SetPaused(_settings.AutomaticPaused);
        shell.Notify += (title, text) => Dispatcher.Invoke(() => _tray.Notify(title, text));
        shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShellViewModel.UpdatesCount)) _tray.SetStatus(shell.UpdatesCount); };

        _services.GetRequiredService<SettingsViewModel>().Saved += async s =>
        {
            _settings = s;
            ApplyTheme(s.Theme);
            await shell.ReloadSettingsAsync();
        };

        if (!_settings.MinimizeToTray || !e.Args.Contains("--minimized")) _window.Show();

        var showEvent = _showEvent;
        new Thread(() =>
        {
            // Background thread: dies with the process instead of racing the dispatcher shutdown.
            while (showEvent.WaitOne())
            {
                try { Dispatcher.Invoke(ShowWindow); }
                catch (TaskCanceledException) { return; }   // dispatcher is shutting down
                catch (InvalidOperationException ex)
                {
                    // Never let one failed show kill the listener: that is what made relaunching do nothing.
                    _services?.GetService<Microsoft.Extensions.Logging.ILogger<App>>()?.LogUnhandled(ex);
                }
            }
        }) { IsBackground = true, Name = "UpdateForge show-window listener" }.Start();

        WatchBackgroundSummary();
        await shell.InitializeAsync();
    }

    private static void ApplyTheme(string theme)
    {
        switch (theme)
        {
            case "Light": ApplicationThemeManager.Apply(ApplicationTheme.Light); break;
            case "Dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark); break;
            default: ApplicationThemeManager.ApplySystemTheme(); break;
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Shows a notification when the scheduled task finished a run while the window was closed.</summary>
    private void WatchBackgroundSummary()
    {
        var dir = Path.GetDirectoryName(BackgroundRunner.SummaryPath)!;
        Directory.CreateDirectory(dir);
        _summaryWatcher = new FileSystemWatcher(dir, Path.GetFileName(BackgroundRunner.SummaryPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        _summaryWatcher.Changed += (_, _) => OnSummary();
        _summaryWatcher.Created += (_, _) => OnSummary();
    }

    private void OnSummary()
    {
        try
        {
            Thread.Sleep(300);
            var s = JsonSerializer.Deserialize<BackgroundRunSummary>(File.ReadAllText(BackgroundRunner.SummaryPath));
            if (s is null) return;
            // FileSystemWatcher raises several events per write; notify once per run.
            lock (_summaryLock)
            {
                if (s.CompletedAt <= _lastSummaryAt) return;
                _lastSummaryAt = s.CompletedAt;
            }
            Dispatcher.Invoke(() =>
            {
                if (s.Failed > 0 && _settings.NotifyFailed) _tray?.Notify("Updates failed", $"{s.Failed} scheduled update(s) failed. Open UpdateForge for details.");
                else if (s.Updated > 0 && _settings.NotifyCompleted) _tray?.Notify("Updates installed", $"{s.Updated} application(s) were updated in the background.");
                else if (s.NeedsApproval > 0) _tray?.Notify("Updates need your approval", $"{s.NeedsApproval} update(s) need your approval.");
                else if (s.UpdatesFound > 0 && _settings.NotifyNewUpdates && s.Updated == 0) _tray?.Notify("Updates available", $"{s.UpdatesFound} update(s) found.");
                if (s.RestartRequired > 0 && _settings.NotifyRestart) _tray?.Notify("Restart required", "A restart is needed to finish some updates.");
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or TaskCanceledException) { /* still being written, or app shutting down; next change will retry */ }
    }

    private void ExitApplication()
    {
        // Exiting mid-install can leave a half-installed application, so ask first.
        if (_services?.GetService<ShellViewModel>() is { IsUpdating: true } &&
            System.Windows.MessageBox.Show("Updates are still installing. Exiting now may leave an application partially updated.\n\nExit anyway?",
                "UpdateForge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (_window is not null) _window.AllowClose = true;
        _tray?.Dispose();
        _summaryWatcher?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}

internal static class LoggingExtensions
{
    public static void LogUnhandled(this Microsoft.Extensions.Logging.ILogger logger, Exception ex) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "Unhandled UI exception");
}
