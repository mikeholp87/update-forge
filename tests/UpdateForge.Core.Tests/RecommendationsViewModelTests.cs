using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Recommendations;
using UpdateForge.Desktop.Services;
using UpdateForge.Desktop.ViewModels;
using Xunit;

namespace UpdateForge.Core.Tests;

/// <summary>The Recommended page's logic (the GUI project is referenced, no window is created): what it shows, and that its switches reach the settings.</summary>
public class RecommendationsViewModelTests
{
    private sealed class Settings : ISettingsStore
    {
        public AppSettings Value { get; set; } = new() { EnabledProviders = ["apt"] };
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class Interests : IInterestStore
    {
        public InterestProfile Value { get; set; } = new();
        public Task<InterestProfile> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(InterestProfile profile, CancellationToken ct) { Value = profile; return Task.CompletedTask; }
    }

    private sealed class History : IHistoryStore
    {
        public Task AddAsync(HistoryRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HistoryRecord>> QueryAsync(string? search, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<HistoryRecord>>([]);
        public Task<DateTimeOffset?> GetLastSuccessAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class Dialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string filterName, string pattern) => Task.FromResult<string?>(null);
    }

    private sealed class Apt : IUpdateProvider
    {
        public string Id => "apt";
        public string DisplayName => "apt";
        public ProviderCapabilities Capabilities { get; } = new(true, true, true, true);
        public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderAvailability(ProviderHealth.Available, "3.0", null));
        public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Source(bool network) : IDiscoverySource
    {
        public bool RequiresNetwork => network;
        public Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(DiscoveryRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<DiscoveredPackage>>([]);
    }

    private static InstalledApplication App(string name) => new() { Id = "apt:" + name, Name = name, ProviderId = "apt", PackageId = name };

    private static (RecommendationsViewModel Vm, Settings Settings) Make(params IDiscoverySource[] sources)
    {
        var settings = new Settings();
        var registry = new ProviderRegistry([new Apt()]);
        var service = new RecommendationService(new PackageManagerService(registry, settings, new TestLock()), settings, new Interests(), null, sources);
        var installer = new PackageInstallService(registry, settings, new History(), new TestLock(), NullLogger<PackageInstallService>.Instance);
        return (new RecommendationsViewModel(service, installer, new Dialogs(), NullLogger<RecommendationsViewModel>.Instance), settings);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "the condition was not met in time");
    }

    [Fact]
    public async Task Without_a_scan_it_asks_for_one()
    {
        var (vm, _) = Make();
        await vm.RefreshAsync();
        Assert.Empty(vm.Packages);
        Assert.Contains("Scan your applications first", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_lists_suggestions_with_the_reason()
    {
        var (vm, _) = Make();
        vm.GetInventory = () => [App("git")];
        await vm.RefreshAsync();
        var row = Assert.Single(vm.Packages, r => r.Key == "git-delta");
        Assert.Equal("Because you have git", row.Because);
        Assert.Equal("apt", row.Manager);
        Assert.True(row.CanInstall);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Dismissing_removes_the_row_and_counts_it()
    {
        var (vm, settings) = Make();
        vm.GetInventory = () => [App("git")];
        await vm.RefreshAsync();
        var row = vm.Packages.First(r => r.Key == "git-delta");

        await vm.DismissCommand.ExecuteAsync(row);

        Assert.DoesNotContain(vm.Packages, r => r.Key == "git-delta");
        Assert.Equal(1, vm.DismissedCount);
        Assert.Contains("git-delta", settings.Value.DismissedRecommendations);
    }

    [Fact]
    public async Task The_lookup_switches_appear_only_when_a_source_exists()
    {
        var (none, _) = Make();
        none.GetInventory = () => [App("git")];
        await none.RefreshAsync();
        Assert.False(none.HasRepositorySource);
        Assert.False(none.HasOnlineSource);

        var (both, _) = Make(new Source(false), new Source(true));
        both.GetInventory = () => [App("git")];
        await both.RefreshAsync();
        Assert.True(both.HasRepositorySource);
        Assert.True(both.HasOnlineSource);
        Assert.True(both.RepositoryDiscovery);     // the local lookup is on by default
        Assert.False(both.OnlineDiscovery);        // the one that uses the network is off until the user opts in
    }

    [Fact]
    public async Task Switching_on_the_online_lookup_is_saved_and_the_page_says_so()
    {
        var (vm, settings) = Make(new Source(false), new Source(true));
        vm.GetInventory = () => [App("git")];
        await vm.RefreshAsync();
        Assert.Contains("Nothing here is fetched from the internet", vm.Message, StringComparison.Ordinal);

        vm.OnlineDiscovery = true;
        await UntilAsync(() => settings.Value.OnlineDiscovery);
        await UntilAsync(() => !vm.IsLoading && vm.Message.Contains("only an interest name is sent", StringComparison.Ordinal));

        vm.RepositoryDiscovery = false;
        await UntilAsync(() => !settings.Value.DiscoverFromRepositories);
        Assert.True(settings.Value.OnlineDiscovery);   // changing one switch leaves the other alone
    }

    [Fact]
    public async Task Turning_learning_off_is_saved()
    {
        var (vm, settings) = Make();
        vm.GetInventory = () => [App("git")];
        await vm.RefreshAsync();
        vm.LearnEnabled = false;
        await UntilAsync(() => !settings.Value.LearnInterests);
    }
}
