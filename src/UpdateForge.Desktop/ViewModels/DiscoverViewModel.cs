using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateForge.Application;
using UpdateForge.Core.Models;

namespace UpdateForge.Desktop.ViewModels;

public sealed record DiscoveredPackageRow(PackageSearchResult Package, string Manager)
{
    public string Name => Package.Name;
    public string PackageId => Package.PackageId;
    public string Version => Package.Version;
    public string Source => Package.Source ?? "Configured sources";
}

public sealed partial class DiscoverViewModel(PackageSearchService service) : ObservableObject
{
    public ObservableCollection<DiscoveredPackageRow> Packages { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _query = "";
    [ObservableProperty] private string _message = "Search for a package by name or ID to get started.";
    [ObservableProperty] private string _providerNotice = "";

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanSearch), IncludeCancelCommand = true)]
    private async Task SearchAsync(CancellationToken ct)
    {
        var query = Query.Trim();
        Packages.Clear();
        ProviderNotice = "";
        Message = $"Searching enabled package managers for “{query}”…";
        try
        {
            var result = await service.SearchAsync(query, ct);
            ct.ThrowIfCancellationRequested();
            var managers = result.Providers.ToDictionary(p => p.ProviderId, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var package in result.Packages)
                Packages.Add(new(package, managers.GetValueOrDefault(package.ProviderId, package.ProviderId)));
            var searched = result.Providers.Count(p => p.Error is null);
            var failed = result.Providers.Count - searched;
            Message = result.Providers.Count == 0
                ? "No searchable package managers are enabled. Enable a manager in Package managers and save changes."
                : searched == 0
                    ? "Search could not complete: no enabled package manager was available. See details below."
                    : Packages.Count == 0
                        ? $"No packages found for “{query}” in {searched} package manager(s)."
                        : $"Found {Packages.Count} package(s) for “{query}” across {searched} package manager(s).";
            if (failed > 0 && searched > 0) Message += $" {failed} manager(s) could not be searched.";
            ProviderNotice = string.Join(Environment.NewLine, result.Providers.Where(p => p.Error is not null)
                .Select(p => $"{p.DisplayName}: {p.Error}"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Message = "Search canceled."; }
        catch (Exception ex) { Message = "Search failed: " + ex.Message; }
    }
}
