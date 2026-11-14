using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UpdateForge.Application;
using UpdateForge.Core.Export;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;
using UpdateForge.Desktop.Services;

namespace UpdateForge.Desktop.ViewModels;

/// <summary>One package manager's share of the inventory, for the dashboard's breakdown bar and list.</summary>
public sealed record ProviderSummary(string Id, string Name, int Count, int Pending, Avalonia.Media.IBrush Brush)
{
    public string PendingText => Pending == 0 ? "nothing pending" : $"{Pending} pending";
    public bool HasPending => Pending > 0;
}

public enum PageId { Dashboard, Applications, Queue, History, PackageManagers, Settings, Discover, Recommendations }

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly InventoryService _inventory;
    private readonly UpdateEngine _engine;
    private readonly ISettingsStore _settingsStore;
    private readonly IExclusionStore _exclusions;
    private readonly IInventoryStore _inventoryStore;
    private readonly IScanHistoryStore _scanHistory;
    private readonly IHistoryStore _history;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ShellViewModel> _log;
    private CancellationTokenSource? _softCancel;
    private HashSet<string> _failedIds = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();

    public ShellViewModel(InventoryService inventory, UpdateEngine engine, ISettingsStore settingsStore, IExclusionStore exclusions,
        IInventoryStore inventoryStore, IScanHistoryStore scanHistory, IHistoryStore history, HistoryViewModel historyVm, SettingsViewModel settingsVm,
        IDialogService dialogs, ILogger<ShellViewModel> log, PackageManagersViewModel managersVm, DiscoverViewModel discoverVm, RecommendationsViewModel recommendationsVm)
    {
        _dialogs = dialogs;
        _inventory = inventory; _engine = engine; _settingsStore = settingsStore; _exclusions = exclusions;
        _inventoryStore = inventoryStore; _scanHistory = scanHistory; _history = history; _log = log;
        HistoryPage = historyVm;
        SettingsPage = settingsVm;
        PackageManagersPage = managersVm;
        DiscoverPage = discoverVm;
        RecommendationsPage = recommendationsVm;
        recommendationsVm.GetInventory = () => [.. Apps.Select(r => r.App)];
        recommendationsVm.PackageInstalled += () => _ = ScanCoreAsync(notify: false);
        recommendationsVm.Notify += (title, text) => { if (_settings.NotifyCompleted) Notify?.Invoke(title, text); };
        discoverVm.IsAlreadyInstalled = p => Apps.Any(r =>
            string.Equals(r.App.PackageId, p.PackageId, StringComparison.OrdinalIgnoreCase) && string.Equals(r.App.ProviderId, p.ProviderId, StringComparison.OrdinalIgnoreCase));
        discoverVm.PackageInstalled += () => _ = ScanCoreAsync(notify: false);
        discoverVm.Notify += (title, text) => { if (_settings.NotifyCompleted) Notify?.Invoke(title, text); };
        managersVm.SettingsChanged = ReloadSettingsAsync;
    }

    public HistoryViewModel HistoryPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public PackageManagersViewModel PackageManagersPage { get; }
    public DiscoverViewModel DiscoverPage { get; }
    public RecommendationsViewModel RecommendationsPage { get; }

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public ObservableCollection<AppRowViewModel> PendingUpdates { get; } = [];
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];
    /// <summary>The rows the Installed applications list shows: <see cref="Apps"/> narrowed by the filter and search box.</summary>
    public ObservableCollection<AppRowViewModel> FilteredApps { get; } = [];
    public Array Filters { get; } = Enum.GetValues<AppFilter>();

    [ObservableProperty] private PageId _currentPage = PageId.Dashboard;
    [ObservableProperty] private AppFilter _filter = AppFilter.All;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AppRowViewModel? _selected;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyPropertyChangedFor(nameof(NoUpdatesNotice)), NotifyPropertyChangedFor(nameof(Headline))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isUpdating;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private bool _overallIndeterminate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subline))] private int _installedCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoUpdatesNotice)), NotifyPropertyChangedFor(nameof(Headline)), NotifyPropertyChangedFor(nameof(HasUpdates))] private int _updatesCount;
    [ObservableProperty] private int _upToDateCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttentionText))] private int _attentionCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subline))] private string _lastScanText = "Never";
    [ObservableProperty] private string _lastSuccessText = "—";
    [ObservableProperty] private string _providerNotice = "";
    [ObservableProperty] private string _automaticText = "Automatic updates: off";
    [ObservableProperty] private string _restartNotice = "";

    /// <summary>True when neither a scan nor an update run is in progress; scan/update buttons bind to this.</summary>
    public bool IsIdle => !IsBusy && !IsUpdating;

    /// <summary>The dashboard's "everything is up to date" line shows only when nothing is pending and no scan is running.</summary>
    /// <summary>The dashboard's one-line answer: what needs the user, or that nothing does.</summary>
    public string Headline => IsBusy ? "Checking…" : UpdatesCount switch { 0 => "Up to date", 1 => "1 update ready", _ => $"{UpdatesCount} updates ready" };

    public bool HasUpdates => UpdatesCount > 0;

    public string Subline => InstalledCount == 0 && LastScanText == "Never" ? "No scan yet. Scan to see what is installed."
        : $"{InstalledCount} packages from {Providers.Count} {(Providers.Count == 1 ? "manager" : "managers")}, last scanned {LastScanText}";

    public string AttentionText => AttentionCount == 0 ? "" : $"{AttentionCount} need a look: held back, excluded, or not updatable automatically.";

    public ObservableCollection<ProviderSummary> Providers { get; } = [];

    private static readonly Dictionary<string, (string Name, string Color)> ProviderStyle = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apt"] = ("apt", "#6F7FFF"),
        ["flatpak"] = ("Flatpak", "#3AA6A0"),
        ["snap"] = ("Snap", "#C76BB4"),
        ["npm"] = ("npm", "#8DB35A"),
        ["pnpm"] = ("pnpm", "#E0A030"),
        ["bun"] = ("Bun", "#E8D5B0"),
        ["yarn"] = ("Yarn", "#4F9FD6"),
        ["cargo"] = ("Cargo", "#D1703C"),
        ["pipx"] = ("pipx", "#5B8FD9"),
        ["brew"] = ("Homebrew", "#D9A93E"),
    };

    private void RebuildProviders()
    {
        Providers.Clear();
        foreach (var g in Apps.GroupBy(r => r.App.ProviderId, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
        {
            var (name, color) = ProviderStyle.TryGetValue(g.Key, out var st) ? st : (g.Key, "#8A94A0");
            Providers.Add(new ProviderSummary(g.Key, name, g.Count(), g.Count(r => r.App.UpdateAvailable && !r.IsExcluded), new SolidColorBrush(Color.Parse(color))));
        }
        OnPropertyChanged(nameof(Subline));
    }

    public bool NoUpdatesNotice => UpdatesCount == 0 && !IsBusy;

    public event Action<string, string>? Notify;

    partial void OnFilterChanged(AppFilter value) => RefreshFilter();
    partial void OnSearchChanged(string value) => RefreshFilter();
    partial void OnCurrentPageChanged(PageId value)
    {
        foreach (var name in (string[])[nameof(IsDashboardPage), nameof(IsApplicationsPage), nameof(IsQueuePage), nameof(IsHistoryPage), nameof(IsManagersPage), nameof(IsSettingsPage), nameof(IsDiscoverPage), nameof(IsRecommendationsPage)])
            OnPropertyChanged(name);
    }

    public bool IsDashboardPage => CurrentPage == PageId.Dashboard;
    public bool IsApplicationsPage => CurrentPage == PageId.Applications;
    public bool IsQueuePage => CurrentPage == PageId.Queue;
    public bool IsHistoryPage => CurrentPage == PageId.History;
    public bool IsManagersPage => CurrentPage == PageId.PackageManagers;
    public bool IsSettingsPage => CurrentPage == PageId.Settings;
    public bool IsDiscoverPage => CurrentPage == PageId.Discover;
    public bool IsRecommendationsPage => CurrentPage == PageId.Recommendations;

    private void RefreshFilter()
    {
        FilteredApps.Clear();
        foreach (var r in Apps.Where(r => r.Matches(Filter) && MatchesSearch(r))) FilteredApps.Add(r);
    }

    private bool MatchesSearch(AppRowViewModel r) =>
        string.IsNullOrWhiteSpace(Search) || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || (r.App.Publisher?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (r.App.PackageId?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);

    [RelayCommand] private async Task NavigateAsync(PageId page)
    {
        CurrentPage = page;
        if (page == PageId.PackageManagers && PackageManagersPage.Managers.Count == 0)
            await PackageManagersPage.RefreshCommand.ExecuteAsync(null);
        if (page == PageId.Recommendations) await RecommendationsPage.RefreshAsync();
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync(default);
        await SettingsPage.LoadAsync();
        UpdateAutomaticText();
        var cached = await _inventoryStore.LoadSnapshotAsync(default);
        Rebuild(cached);
        await RefreshMetaAsync();
        if (_settings.CheckAtStartup) await ScanAsync();
    }

    [RelayCommand]
    private Task ScanAsync() => ScanCoreAsync(notify: true);

    private async Task ScanCoreAsync(bool notify)
    {
        if (IsBusy || IsUpdating) return;
        IsBusy = true;
        OverallIndeterminate = true;
        try
        {
            _settings = await _settingsStore.LoadAsync(default);
            var progress = new Progress<string>(m => Status = m);
            var result = await _inventory.ScanAsync(_settings, progress, default);
            Rebuild(result.Applications);
            _ = RecommendationsPage.LearnAsync(result.Applications);   // notice what is new since the last scan
            var problems = result.Providers.Where(p => p.Availability.Health != ProviderHealth.NotInstalled && (!p.Availability.IsAvailable || p.Error is not null)).ToList();
            ProviderNotice = problems.Count == 0 ? "" : string.Join("  ", problems.Select(p => $"{p.ProviderId}: {p.Error ?? p.Availability.Message}"));
            Status = $"Scan complete: {result.Applications.Count} applications in {result.Duration.TotalSeconds:F1}s.";
            if (notify && UpdatesCount > 0 && _settings.NotifyNewUpdates) Notify?.Invoke("Updates available", $"{UpdatesCount} application update(s) can be installed.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan failed");
            Status = "Scan failed: " + ex.Message;
        }
        finally { IsBusy = false; OverallIndeterminate = false; await RefreshMetaAsync(); }
    }

    private void Rebuild(IReadOnlyList<InstalledApplication> apps)
    {
        var keepSelected = Selected?.App.Id;
        Apps.Clear();
        foreach (var a in apps) Apps.Add(new AppRowViewModel(a, _failedIds.Contains(a.Id)));
        PendingUpdates.Clear();
        foreach (var r in Apps.Where(r => r.App.UpdateAvailable && !r.IsExcluded)) PendingUpdates.Add(r);
        InstalledCount = Apps.Count;
        UpdatesCount = PendingUpdates.Count;
        UpToDateCount = Apps.Count(r => r.App.Status == UpdateStatus.UpToDate);
        AttentionCount = Apps.Count(r => r.NeedsAttention);
        Selected = keepSelected is null ? null : Apps.FirstOrDefault(r => r.App.Id == keepSelected);
        RebuildProviders();
        if (CurrentPage == PageId.Recommendations) _ = RecommendationsPage.RefreshAsync();   // the inventory changed under an open page
        RefreshFilter();
        DiscoverPage.RefreshInstalledState();
    }

    private async Task RefreshMetaAsync()
    {
        var last = await _scanHistory.GetLastAsync(default);
        LastScanText = last is null ? "Never" : last.StartedAt.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        var ok = await _history.GetLastSuccessAsync(default);
        LastSuccessText = ok is null ? "—" : ok.Value.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        await HistoryPage.RefreshAsync();
    }

    private void UpdateAutomaticText() =>
        AutomaticText = _settings.Mode == AutomaticMode.Manual ? "Automatic installation: off (manual)" : "Automatic installation: safe updates only";

    [RelayCommand]
    private Task UpdateAllAsync() => RunUpdatesAsync(Apps.Where(r => r.CanUpdate && !r.IsExcluded).Select(r => r.App).ToList());

    [RelayCommand]
    private Task UpdateCheckedAsync() => RunUpdatesAsync(Apps.Where(r => r.IsChecked).Select(r => r.App).ToList());

    /// <summary>Updates exactly one application (the row's Update button). Same engine, policy and verification as Update all.</summary>
    [RelayCommand]
    private Task UpdateOneAsync(AppRowViewModel? row)
    {
        if (row is null || !row.CanUpdate) return Task.CompletedTask;
        return RunUpdatesAsync([row.App]);
    }

    [RelayCommand]
    private Task UpdateSelectedAsync() => Selected is null ? Task.CompletedTask : RunUpdatesAsync([Selected.App]);

    [RelayCommand]
    private Task RetryFailedAsync() => RunUpdatesAsync(Apps.Where(r => r.Failed).Select(r => r.App with { }).ToList());

    private async Task RunUpdatesAsync(List<InstalledApplication> targets)
    {
        if (targets.Count == 0) { Status = "Nothing to update."; return; }
        if (IsUpdating || IsBusy)
        {
            Status = "Another scan or update is in progress. Wait for it to finish, then try again.";
            return;
        }
        IsUpdating = true;
        RestartNotice = "";
        _failedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Queue.Clear();
        foreach (var t in targets) Queue.Add(new QueueItemViewModel(t.Id, t.Name));
        OverallProgress = 0;
        CurrentPage = PageId.Queue;
        _softCancel = new CancellationTokenSource();

        var progress = new Progress<EngineProgress>(OnEngineProgress);
        try
        {
            // Inside the try so a store failure can never leave IsUpdating stuck on (which would lock every button).
            var policy = new UpdatePolicySettings
            {
                Mode = _settings.Mode,
                ExcludedApplicationIds = await _exclusions.GetExcludedAsync(default),
                SkippedVersions = await _exclusions.GetSkippedVersionsAsync(default),
                BlockedSources = new HashSet<string>(_settings.BlockedSources, StringComparer.OrdinalIgnoreCase),
                EnabledProviders = new HashSet<string>(_settings.EnabledProviders, StringComparer.OrdinalIgnoreCase),
            };

            // Explicit user click: unattended=false. Cancel Pending is a soft cancel; running installers finish.
            var results = await Task.Run(() => _engine.RunAsync(targets, policy, false, progress, _settings.MaxParallelUpdates, _softCancel.Token));
            _failedIds = results.Where(r => r.FinalState is JobState.Failed or JobState.Blocked).Select(r => r.Application.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var done = results.Count(r => r.FinalState == JobState.Completed);
            var failed = results.Count(r => r.FinalState is JobState.Failed or JobState.Blocked);
            var restart = results.Count(r => r.FinalState == JobState.RestartRequired);
            var manual = results.Count(r => r.FinalState == JobState.ManualActionRequired);
            Status = $"{done} updated, {failed} failed, {manual} need attention, {restart} need a restart.";
            if (restart > 0) RestartNotice = $"{restart} update(s) need a restart to finish. UpdateForge never restarts automatically.";
            if (failed > 0 && _settings.NotifyFailed) Notify?.Invoke("Some updates failed", Status);
            else if (_settings.NotifyCompleted) Notify?.Invoke("Updates finished", Status);
            if (restart > 0 && _settings.NotifyRestart) Notify?.Invoke("Restart required", RestartNotice);
        }
        catch (EngineBusyException) { Status = "Another UpdateForge update run is already in progress."; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update run failed");
            Status = "Update run failed: " + ex.Message;
        }
        finally
        {
            IsUpdating = false;
            _softCancel?.Dispose();
            _softCancel = null;
        }
        await ScanCoreAsync(notify: false); // cache invalidation: re-read real installed versions (no "updates available" toast right after updating)
    }

    private void OnEngineProgress(EngineProgress p)
    {
        var item = Queue.FirstOrDefault(q => q.ApplicationId == p.ApplicationId);
        if (item is null) return;
        item.State = FriendlyState(p.State);
        item.Message = p.Message;
        item.IsIndeterminate = p.Fraction is null && !JobStateMachine.IsTerminal(p.State);
        if (p.Fraction is { } f) item.Fraction = f;
        item.IsFinished = JobStateMachine.IsTerminal(p.State);
        if (item.IsFinished) item.Fraction = 1;
        OverallProgress = Queue.Count == 0 ? 0 : Queue.Count(q => q.IsFinished) * 100.0 / Queue.Count;
        Status = $"[{p.Index}/{p.Total}] {p.ApplicationName}: {p.Message}";
    }

    private static string FriendlyState(JobState s) => s switch
    {
        JobState.VerifyingInstallation => "Verifying",
        JobState.RestartRequired => "Restart required",
        JobState.ManualActionRequired => "Needs attention",
        _ => s.ToString(),
    };

    [RelayCommand]
    private void CancelPending() { _softCancel?.Cancel(); Status = "Canceling pending updates; the running installer will finish first."; }

    [RelayCommand]
    private async Task ToggleExcludeAsync(AppRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;
        await _exclusions.SetExcludedAsync(row.App.Id, row.App.Name, !row.IsExcluded, default);
        Rebuild([.. Apps.Select(r => r.App.Id == row.App.Id ? r.App with { IsExcluded = !r.IsExcluded } : r.App)]);
    }

    [RelayCommand]
    private async Task SkipVersionAsync()
    {
        if (Selected?.App is { AvailableVersion: { } v } app && app.UpdateAvailable)
        {
            await _exclusions.AddSkippedVersionAsync(app.Id, v, default);
            Status = $"Version {v} of {app.Name} will be skipped. A newer version will still be offered.";
        }
    }

    /// <summary>Exports exactly what the Installed applications list is showing (respecting the search and filter) as CSV.</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var rows = FilteredApps.Select(r => r.App).ToList();
        var path = await _dialogs.PickSaveFileAsync("Export applications", $"updateforge-apps-{DateTime.Now:yyyyMMdd}.csv", "CSV file", "*.csv");
        if (path is null) return;
        try { CsvExporter.WriteFile(path, CsvExporter.Applications(rows)); Status = $"Exported {rows.Count} applications to {path}."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "Could not write the file: " + ex.Message; }
    }

    public async Task ReloadSettingsAsync()
    {
        _settings = await _settingsStore.LoadAsync(default);
        UpdateAutomaticText();
    }
}
