using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Providers;
using UpdateForge.Desktop.Services;
using UpdateForge.Desktop.ViewModels;
using UpdateForge.Infrastructure;
using UpdateForge.Providers.Linux;
using UpdateForge.Providers.Npm;

namespace UpdateForge.Desktop;

public sealed class App : Avalonia.Application
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private FileSystemWatcher? _summaryWatcher;
    private AppSettings _settings = new();
    private readonly object _summaryLock = new();
    private DateTimeOffset _lastSummaryAt = DateTimeOffset.UtcNow;   // ignore a summary left over from before this launch

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            _ = StartAsync(lifetime.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(string[] args)
    {
        try { await StartCoreAsync(args); }
        catch (Exception ex)
        {
            // ShutdownMode is OnExplicitShutdown, so a startup failure (corrupt database, newer schema, ...) would otherwise leave an invisible process.
            Console.Error.WriteLine("UpdateForge could not start: " + ex.Message);
            _tray?.Dispose();
            _lifetime?.Shutdown(1);
        }
    }

    private async Task StartCoreAsync(string[] args)
    {
        _services = new ServiceCollection()
            .AddUpdateForgeCore()
            .AddSingleton<IUpdateProvider, AptProvider>()
            .AddSingleton<IUpdateProvider, FlatpakProvider>()
            .AddSingleton<IUpdateProvider, SnapProvider>()
            .AddSingleton<IUpdateProvider, NpmProvider>()
            .AddSingleton<IUpdateProvider, PnpmProvider>()
            .AddSingleton<IUpdateProvider, BunProvider>()
            .AddSingleton<IUpdateProvider, YarnProvider>()
            .AddSingleton<IUpdateProvider, CargoProvider>()
            .AddSingleton<IUpdateProvider, PipxProvider>()
            .AddSingleton<IUpdateProvider, HomebrewProvider>()
            .AddSingleton<IScheduleService, SystemdScheduleService>()
            .AddSingleton<IDialogService, DialogService>()
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
        _window.ExitRequested = () => Dispatcher.UIThread.Post(ExitApplication);
        _lifetime!.MainWindow = _window;

        CreateTray(shell, settingsStore);
        shell.Notify += (title, text) => Notifier.Send(title, text);
        shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.UpdatesCount)) _tray!.ToolTipText = shell.UpdatesCount > 0 ? $"UpdateForge - {shell.UpdatesCount} update(s) available" : "UpdateForge - up to date"; };

        _services.GetRequiredService<SettingsViewModel>().Saved += async s =>
        {
            _settings = s;
            ApplyTheme(s.Theme);
            await shell.ReloadSettingsAsync();
        };

        // --page=Applications|Discover|Queue|History|PackageManagers|Settings opens that page first (for launcher actions and docs).
        if (args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.Ordinal)) is { } pageArg && Enum.TryParse<PageId>(pageArg["--page=".Length..], ignoreCase: true, out var page))
            shell.NavigateCommand.Execute(page);

        if (!_settings.MinimizeToTray || !args.Contains("--minimized")) _window.Show();
        Program.Single.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
        WatchBackgroundSummary();
        await shell.InitializeAsync();
    }

    private void CreateTray(ShellViewModel shell, ISettingsStore settingsStore)
    {
        _pauseItem = new NativeMenuItem("Pause automatic updates") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _settings.AutomaticPaused };
        _pauseItem.Click += async (_, _) =>
        {
            try
            {
                var paused = _pauseItem.IsChecked;
                var s = await settingsStore.LoadAsync(default);
                if (s.AutomaticPaused == paused) return;
                await settingsStore.SaveAsync(s with { AutomaticPaused = paused }, default);
                _settings = _settings with { AutomaticPaused = paused };
            }
            catch (Exception ex) { _services?.GetService<ILogger<App>>()?.LogError(ex, "Could not save the pause setting"); }   // async void: must not escape
        };

        NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => Dispatcher.UIThread.Post(action);
            return item;
        }

        var menu = new NativeMenu
        {
            Item("Open UpdateForge", ShowWindow),
            Item("Scan for updates", () => shell.ScanCommand.Execute(null)),
            Item("Update approved applications", () => { ShowWindow(); shell.UpdateAllCommand.Execute(null); }),
            _pauseItem,
            Item("View update history", () => { ShowWindow(); shell.NavigateCommand.Execute(PageId.History); }),
            Item("Settings", () => { ShowWindow(); shell.NavigateCommand.Execute(PageId.Settings); }),
            new NativeMenuItemSeparator(),
            Item("Exit", ExitApplication),
        };
        _tray = new TrayIcon
        {
            ToolTipText = "UpdateForge",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://updateforge/Assets/UpdateForge-512.png"))),
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void ApplyTheme(string theme) => RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Shows a notification when the scheduled timer finished a run while the window was closed.</summary>
    private void WatchBackgroundSummary()
    {
        var dir = Path.GetDirectoryName(BackgroundRunner.SummaryPath)!;
        Directory.CreateDirectory(dir);
        _summaryWatcher = new FileSystemWatcher(dir, Path.GetFileName(BackgroundRunner.SummaryPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        _summaryWatcher.Changed += (_, _) => OnSummary();
        _summaryWatcher.Created += (_, _) => OnSummary();
        _summaryWatcher.Renamed += (_, _) => OnSummary();   // the runner writes a temp file and renames it into place
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
            if (s.Failed > 0 && _settings.NotifyFailed) Notifier.Send("Updates failed", $"{s.Failed} scheduled update(s) failed. Open UpdateForge for details.");
            else if (s.Updated > 0 && _settings.NotifyCompleted) Notifier.Send("Updates installed", $"{s.Updated} application(s) were updated in the background.");
            else if (s.NeedsApproval > 0) Notifier.Send("Updates need your approval", $"{s.NeedsApproval} update(s) need your approval.");
            else if (s.UpdatesFound > 0 && _settings.NotifyNewUpdates && s.Updated == 0) Notifier.Send("Updates available", $"{s.UpdatesFound} update(s) found.");
            if (s.RestartRequired > 0 && _settings.NotifyRestart) Notifier.Send("Restart required", "A restart is needed to finish some updates.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* still being written; the next change retries */ }
    }

    private async void ExitApplication()
    {
        try
        {
            // Exiting mid-install can leave a half-installed application, so ask first.
            if (_services?.GetService<ShellViewModel>() is { IsUpdating: true } &&
                !await _services.GetRequiredService<IDialogService>().ConfirmAsync("UpdateForge",
                    "Updates are still installing. Exiting now may leave an application partially updated.\n\nExit anyway?"))
                return;
            if (_window is not null) _window.AllowClose = true;
            _tray?.Dispose();
            _summaryWatcher?.Dispose();
            _services?.Dispose();
            _lifetime?.Shutdown();
        }
        catch (Exception ex) { _services?.GetService<ILogger<App>>()?.LogError(ex, "Exit failed"); }
    }
}
