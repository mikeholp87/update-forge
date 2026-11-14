using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Recommendations;
using UpdateForge.Core.Updates;
using UpdateForge.Desktop.Services;

namespace UpdateForge.Desktop.ViewModels;

public sealed partial class RecommendationRow(Recommendation item) : ObservableObject
{
    private static readonly Dictionary<string, string> ManagerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apt"] = "apt", ["flatpak"] = "Flatpak", ["snap"] = "Snap", ["npm"] = "npm", ["pnpm"] = "pnpm", ["bun"] = "Bun",
        ["yarn"] = "Yarn", ["cargo"] = "Cargo", ["pipx"] = "pipx", ["brew"] = "Homebrew",
    };

    public Recommendation Item { get; } = item;
    public string Key => Item.Key;
    public string Title => Item.Title;
    public string Why => Item.Why;
    // Tips write commands in backticks; the row shows plain text, so the markers are dropped.
    public string Tip => (Item.Tip ?? "").Replace("`", "", StringComparison.Ordinal);
    public bool HasTip => Item.Tip is not null;
    public bool IsPackage => Item.Kind == RecommendationKind.Package;
    public string Manager => Item.Package is { } p ? (ManagerNames.TryGetValue(p.ProviderId, out var n) ? n : p.ProviderId) : "";
    public string Meta => !IsPackage ? Item.Category
        : string.Equals(Item.Category, Manager, StringComparison.OrdinalIgnoreCase) ? "from " + Manager : $"{Item.Category}, from {Manager}";
    public string Because => Item.Because ?? "";
    public bool HasBecause => Item.Because is not null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall))] private bool _isInstalling;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall))] private bool _isDone;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = "";

    public bool HasStatus => Status.Length > 0;
    public bool CanInstall => IsPackage && !IsInstalling && !IsDone;
}

/// <summary>One thing it has learned about the user, for the "what I have learned" strip.</summary>
public sealed record InterestRow(string Name, string Strength, string Detail);

/// <summary>
/// Suggestions built from what is installed, with a tip for each. The catalog is local and reviewed: nothing is fetched and nothing
/// about this machine leaves it. Installing goes through the same confirmed, validated, recorded path as Discover.
/// </summary>
public sealed partial class RecommendationsViewModel(RecommendationService service, PackageInstallService installer, IDialogService dialogs,
    ILogger<RecommendationsViewModel> log) : ObservableObject
{
    private bool _installBusy;

    public ObservableCollection<RecommendationRow> Packages { get; } = [];
    public ObservableCollection<RecommendationRow> Tips { get; } = [];

    /// <summary>Set by the shell: the current inventory, which the suggestions are based on.</summary>
    public Func<IReadOnlyList<InstalledApplication>>? GetInventory { get; set; }

    /// <summary>Raised after an install so the shell can rescan.</summary>
    public event Action? PackageInstalled;

    public event Action<string, string>? Notify;

    public ObservableCollection<InterestRow> Interests { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _learnEnabled = true;
    private bool _syncing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDismissed))] private int _dismissedCount;
    [ObservableProperty] private string _headline = "Recommended for you";
    [ObservableProperty] private string _message = "";

    public bool HasDismissed => DismissedCount > 0;
    public string RestoreText => $"Bring back {DismissedCount} dismissed";
    public bool HasTips => Tips.Count > 0;
    public bool HasPackages => Packages.Count > 0;
    public bool HasInterests => Interests.Count > 0;
    public string LearnedText => !LearnEnabled ? "Learning is off. Suggestions use only what is installed right now."
        : Interests.Count == 0 ? "Nothing learned yet. It picks up your interests as you install apps." : "What it has learned about you";

    partial void OnLearnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(LearnedText));
        if (_syncing) return;
        _ = ApplyLearningAsync(value);
    }

    private async Task ApplyLearningAsync(bool value)
    {
        try { await service.SetLearningAsync(value, default); await RefreshAsync(); }
        catch (Exception ex) { Message = "Could not save that choice: " + ex.Message; }
    }

    /// <summary>Called after every scan so it keeps learning even when this page is never opened.</summary>
    public async Task LearnAsync(IReadOnlyList<InstalledApplication> apps)
    {
        try { await service.LearnAsync(apps, default); }
        catch (Exception ex) { log.LogWarning(ex, "Could not update learned interests"); }
    }

    [RelayCommand]
    private async Task ForgetAsync()
    {
        if (!await dialogs.ConfirmAsync("Forget what it learned", "Erase everything learned about your interests? Suggestions go back to using only what is installed right now. This cannot be undone."))
            return;
        await service.ForgetAsync(default);
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var apps = GetInventory?.Invoke() ?? [];
            if (apps.Count == 0)
            {
                Packages.Clear(); Tips.Clear();
                Message = "Scan your applications first. Suggestions are based on what is installed.";
                return;
            }
            var report = await service.GetAsync(apps, default);
            Packages.Clear(); Tips.Clear();
            foreach (var item in report.Items)
                (item.Kind == RecommendationKind.Package ? Packages : Tips).Add(new RecommendationRow(item));
            DismissedCount = report.DismissedCount;
            _syncing = true;
            LearnEnabled = report.Learning;
            _syncing = false;
            Interests.Clear();
            foreach (var i in report.Interests.Take(5))
                Interests.Add(new InterestRow(i.Name, i.Strength, i.Apps.Count > 0 ? string.Join(", ", i.Apps) : i.Learned ? "learned from what you install" : "from your developer tools"));
            OnPropertyChanged(nameof(RestoreText)); OnPropertyChanged(nameof(HasTips)); OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(HasInterests)); OnPropertyChanged(nameof(LearnedText));
            Message = Packages.Count + Tips.Count == 0
                ? "Nothing to suggest right now. Your setup already covers what this list knows about."
                : $"{Packages.Count} {(Packages.Count == 1 ? "package fits" : "packages fit")} what you have installed. Nothing here is fetched from the internet or ranked by popularity.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not build recommendations");
            Message = "Could not build suggestions: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task DismissAsync(RecommendationRow? row)
    {
        if (row is null) return;
        try
        {
            await service.DismissAsync(row.Item, default);
            Packages.Remove(row); Tips.Remove(row);
            DismissedCount++;
            OnPropertyChanged(nameof(RestoreText)); OnPropertyChanged(nameof(HasTips)); OnPropertyChanged(nameof(HasPackages));
        }
        catch (Exception ex) { Message = "Could not save that choice: " + ex.Message; }
    }

    [RelayCommand]
    private async Task RestoreDismissedAsync()
    {
        await service.RestoreDismissedAsync(default);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InstallAsync(RecommendationRow? row)
    {
        if (row is not { CanInstall: true, Item.Package: { } package } || _installBusy) return;

        var admin = package.ProviderId.ToLowerInvariant() switch
        {
            "apt" => "\n\nThis installs the package system-wide and asks for your administrator password. Packages can run scripts as root while they install.",
            "snap" or "flatpak" => "\n\nThis may ask for your administrator password.",
            "npm" or "pnpm" or "bun" or "yarn" => "\n\nThis installs the package globally. Node packages can run scripts while they install, so check the name first.",
            "cargo" => "\n\nThis compiles the program from source, which can take a few minutes.",
            _ => "",
        };
        if (!await dialogs.ConfirmAsync("Install " + row.Title,
                $"Install {row.Title}?\n\nPackage: {package.PackageId}\nManager: {row.Manager}\n\n{row.Why}{admin}\n\nOnly install software you trust. UpdateForge runs the package manager's own installer and does not review the package."))
            return;

        _installBusy = true;
        row.IsInstalling = true;
        row.Status = "Installing…";
        try
        {
            var outcome = await Task.Run(() => installer.InstallAsync(new PackageSearchResult(package.ProviderId, package.PackageId, row.Title, "latest", package.Source),
                new Progress<string>(m => row.Status = m), CancellationToken.None));
            if (outcome.Installed || outcome.State == JobState.Skipped)
            {
                row.IsDone = true;
                row.Status = outcome.RestartRequired ? "Installed. Restart to finish." : "Installed";
                await service.RecordInstalledAsync(row.Item, default);
                Notify?.Invoke("Package installed", row.Title + " was installed.");
                PackageInstalled?.Invoke();
            }
            else row.Status = outcome.State == JobState.ManualActionRequired ? "Needs attention: " + outcome.Message : "Not installed: " + outcome.Message;
        }
        catch (EngineBusyException) { row.Status = "Another update or install is running. Try again when it finishes."; }
        catch (Exception ex)
        {
            log.LogError(ex, "Install from recommendations failed for {Package}", package.PackageId);
            row.Status = "Could not install: " + ex.Message;
        }
        finally { row.IsInstalling = false; _installBusy = false; }
    }
}
