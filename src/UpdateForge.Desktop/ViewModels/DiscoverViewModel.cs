using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class DiscoveredPackageRow(PackageSearchResult package, string manager) : ObservableObject
{
    public PackageSearchResult Package { get; } = package;
    public string Manager { get; } = manager;
    public string Name => Package.Name;
    public string PackageId => Package.PackageId;
    public string Version => Package.Version;
    public string Source => Package.Source ?? "Configured sources";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall))] private bool _isInstalled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall))] private bool _isInstalling;
    [ObservableProperty] private string _status = "";

    public bool CanInstall => !IsInstalled && !IsInstalling;
}

public sealed partial class DiscoverViewModel(PackageSearchService service, PackageInstallService installer, ILogger<DiscoverViewModel> log) : ObservableObject
{
    private readonly SearchResultCollection _packages = [];
    private bool _sortInitialized;
    private bool _cancelRequested;
    public ObservableCollection<DiscoveredPackageRow> Packages => _packages;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand)), NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string _query = "";
    [ObservableProperty] private string _message = "Search for a package by name or ID to get started.";
    [ObservableProperty] private string _providerNotice = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand)), NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _installRunning;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand)), NotifyCanExecuteChangedFor(nameof(RefreshCommand)),
        NotifyCanExecuteChangedFor(nameof(CancelSearchCommand))]
    private bool _isSearching;

    /// <summary>Lets the shell mark results that are already installed on this PC. Set by the shell.</summary>
    public Func<PackageSearchResult, bool>? IsAlreadyInstalled { get; set; }

    /// <summary>Raised after a package was installed so the shell can rescan the inventory.</summary>
    public event Action? PackageInstalled;

    /// <summary>Raised with (title, text) when a background install finishes, for a tray notification.</summary>
    public event Action<string, string>? Notify;

    private bool CanSearch() => !IsSearching && !InstallRunning && !string.IsNullOrWhiteSpace(Query);
    private bool CanCancelSearch() => IsSearching && !_cancelRequested;

    [RelayCommand(CanExecute = nameof(CanSearch), IncludeCancelCommand = true)]
    private Task SearchAsync(CancellationToken ct) => SearchCoreAsync(refresh: false, ct);

    [RelayCommand(CanExecute = nameof(CanSearch), IncludeCancelCommand = true)]
    private Task RefreshAsync(CancellationToken ct) => SearchCoreAsync(refresh: true, ct);

    [RelayCommand(CanExecute = nameof(CanCancelSearch))]
    private void CancelSearch()
    {
        _cancelRequested = true;
        SearchCommand.Cancel();
        RefreshCommand.Cancel();
        CancelSearchCommand.NotifyCanExecuteChanged();
    }

    private async Task SearchCoreAsync(bool refresh, CancellationToken ct)
    {
        if (IsSearching || InstallRunning) return;
        _cancelRequested = false;
        IsSearching = true;
        var results = new List<PackageSearchProviderResult>();
        var query = Query.Trim();
        Packages.Clear();
        ProviderNotice = "";
        Message = $"Searching enabled package managers for “{query}”…";
        try
        {
            if (!_sortInitialized)
            {
                CollectionViewSource.GetDefaultView(Packages).SortDescriptions.Add(new(nameof(DiscoveredPackageRow.Name), ListSortDirection.Ascending));
                _sortInitialized = true;
            }
            await foreach (var completed in service.SearchProvidersAsync(query, ct, refresh))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(completed);
                _packages.Append(completed.Packages.Select(package =>
                {
                    var row = new DiscoveredPackageRow(package, completed.DisplayName);
                    if (IsAlreadyInstalled?.Invoke(package) == true) { row.IsInstalled = true; row.Status = "Installed"; }
                    return row;
                }));
                ProviderNotice = string.Join(Environment.NewLine, results.Where(p => p.Error is not null)
                    .Select(p => $"{p.DisplayName}: {p.Error}"));
                if (!InstallRunning) Message = $"Found {Packages.Count} package(s) so far for “{query}”. Searching remaining managers…";
            }
            ct.ThrowIfCancellationRequested();
            var searched = results.Count(p => p.Error is null);
            var failed = results.Count - searched;
            var summary = results.Count == 0
                ? "No searchable package managers are enabled. Enable a manager in Package managers and save changes."
                : searched == 0
                    ? "Search could not complete: no enabled package manager was available. See details below."
                    : Packages.Count == 0
                        ? $"No packages found for “{query}” in {searched} package manager(s)."
                        : $"Found {Packages.Count} package(s) for “{query}” across {searched} package manager(s).";
            if (failed > 0 && searched > 0) summary += $" {failed} manager(s) could not be searched.";
            if (results.Any(r => r.IsCached)) summary += " Showing recent results. Use Refresh to search again.";
            if (!InstallRunning) Message = summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!InstallRunning) Message = Packages.Count == 0 ? "Search canceled." : $"Search canceled. Kept {Packages.Count} package(s) already found.";
        }
        catch (Exception ex) { if (!InstallRunning) Message = "Search failed: " + ex.Message; }
        finally { IsSearching = false; }
    }

    // WPF refreshes its grid once per completed manager, rather than once for every package added.
    private sealed class SearchResultCollection : ObservableCollection<DiscoveredPackageRow>
    {
        public void Append(IEnumerable<DiscoveredPackageRow> rows)
        {
            CheckReentrancy();
            var before = Count;
            foreach (var row in rows) Items.Add(row);
            if (Count == before) return;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>Marks rows as installed or not after the inventory changed (for example after a rescan).</summary>
    public void RefreshInstalledState()
    {
        if (IsAlreadyInstalled is null) return;
        foreach (var row in Packages.Where(r => !r.IsInstalling))
        {
            var installed = IsAlreadyInstalled(row.Package);
            row.IsInstalled = installed;
            row.Status = installed ? "Installed" : "";
        }
    }

    [RelayCommand]
    private async Task InstallAsync(DiscoveredPackageRow? row)
    {
        if (row is null || !row.CanInstall || InstallRunning) return;

        var source = row.Package.Source is null ? "" : $"\nSource: {row.Package.Source}";
        var admin = row.Package.ProviderId.ToLowerInvariant() switch
        {
            "chocolatey" => "\n\nChocolatey installs need UpdateForge to be running as administrator.",
            "npm" => "\n\nThis installs the package globally with npm. npm packages can run scripts during installation, and anyone can publish one, so check the name and publisher first.",
            _ => "",
        };
        var answer = System.Windows.MessageBox.Show(
            $"Install {row.Name}?\n\nPackage: {row.PackageId}\nVersion: {row.Version}\nManager: {row.Manager}{source}{admin}\n\n" +
            "Only install software you trust. UpdateForge runs the package manager's own installer and does not review the package.",
            "Install package", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        InstallRunning = true;
        row.IsInstalling = true;
        row.Status = "Installing…";
        Message = $"Installing {row.Name}…";
        try
        {
            var outcome = await Task.Run(() => installer.InstallAsync(row.Package, new Progress<string>(m => Message = m), CancellationToken.None));
            if (outcome.Installed)
            {
                row.IsInstalled = true;
                row.Status = outcome.RestartRequired ? "Installed (restart required)" : "Installed";
                Message = $"{row.Name} was installed." + (outcome.RestartRequired ? " A restart is required to finish." : "");
                Notify?.Invoke("Package installed", Message);
                PackageInstalled?.Invoke();
            }
            else if (outcome.State == JobState.Skipped)
            {
                row.IsInstalled = true;
                row.Status = "Installed";
                Message = $"{row.Name} is already installed.";
                PackageInstalled?.Invoke();
            }
            else
            {
                row.Status = outcome.State == JobState.ManualActionRequired ? "Needs attention" : "Failed";
                Message = $"{row.Name} was not installed: {outcome.Message} See History for details.";
            }
        }
        catch (EngineBusyException) { row.Status = ""; Message = "Another UpdateForge update or install is already running. Try again when it finishes."; }
        catch (Exception ex)
        {
            log.LogError(ex, "Install from Discover failed for {Package}", row.PackageId);
            row.Status = "Failed";
            Message = $"{row.Name} could not be installed: {ex.Message}";
        }
        finally { row.IsInstalling = false; InstallRunning = false; }
    }
}
