using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Providers.WinGet;
using Xunit;

namespace UpdateForge.Core.Tests;

public class PackageManagerTests
{
    private sealed class Settings : ISettingsStore
    {
        public AppSettings Value { get; set; } = new();
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class Manager : IUpdateProvider, IPackageManager
    {
        public string Id => "test";
        public string DisplayName => "Test manager";
        public string ExecutablePath => "test.exe";
        public bool CanRefreshSources => true;
        public bool Broken { get; set; }
        public bool Canceled { get; set; }
        public int Refreshes { get; private set; }
        public ProviderCapabilities Capabilities { get; } = new(true, true, true, true);
        public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) =>
            Canceled ? throw new OperationCanceledException(cancellationToken) :
            Broken ? throw new InvalidOperationException("Tool failed") :
            Task.FromResult(new ProviderAvailability(ProviderHealth.Available, "2.0", null));
        public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetSourcesAsync(CancellationToken cancellationToken) => Task.FromResult("main https://example.org");
        public Task<string> RefreshSourcesAsync(CancellationToken cancellationToken) { Refreshes++; return Task.FromResult("Refreshed"); }
    }

    [Fact]
    public async Task Saving_enabled_managers_preserves_other_settings_and_unknown_providers()
    {
        var store = new Settings { Value = new AppSettings { Theme = "Light", CustomIntervalHours = 72, EnabledProviders = ["test", "future"] } };
        var service = new PackageManagerService(new ProviderRegistry([new Manager()]), store, new TestLock());
        await service.SaveEnabledAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(["future"], store.Value.EnabledProviders);
        Assert.Equal("Light", store.Value.Theme);
        Assert.Equal(72, store.Value.CustomIntervalHours);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveEnabledAsync(["invalid"], TestContext.Current.CancellationToken));
        Assert.Equal(["future"], store.Value.EnabledProviders);
    }

    [Fact]
    public async Task One_broken_manager_does_not_hide_the_other_statuses()
    {
        var service = new PackageManagerService(new ProviderRegistry([new Manager { Broken = true }, new FakeProvider()]), new Settings(), new TestLock());
        var statuses = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, statuses.Count);
        Assert.Equal(ProviderHealth.Unavailable, statuses.Single(s => s.Id == "test").Availability.Health);
        Assert.Equal(ProviderHealth.Available, statuses.Single(s => s.Id == "fake").Availability.Health);
    }

    [Fact]
    public async Task Status_cancellation_propagates()
    {
        var service = new PackageManagerService(new ProviderRegistry([new Manager { Canceled = true }]), new Settings(), new TestLock());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetStatusAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task Refresh_respects_enabled_state_and_the_shared_update_lock()
    {
        var manager = new Manager();
        var store = new Settings();
        var gate = new TestLock();
        var service = new PackageManagerService(new ProviderRegistry([manager]), store, gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshSourcesAsync("test", TestContext.Current.CancellationToken));
        store.Value = store.Value with { EnabledProviders = ["test"] };
        gate.Busy = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshSourcesAsync("test", TestContext.Current.CancellationToken));
        Assert.Equal(0, manager.Refreshes);
        gate.Busy = false;
        Assert.Equal("Refreshed", await service.RefreshSourcesAsync("test", TestContext.Current.CancellationToken));
        Assert.Equal(1, manager.Refreshes);
    }

    [Fact]
    public async Task WinGet_sources_use_fixed_arguments_and_report_native_failures()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "winget https://example.org", "", false));
        var provider = new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, "winget.exe");
        Assert.Contains("winget", await provider.GetSourcesAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await provider.RefreshSourcesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["source", "list", "--disable-interactivity"], runner.Calls[0].Arguments);
        Assert.Equal(["source", "update", "--disable-interactivity"], runner.Calls[1].Arguments);
        var failed = new WinGetProvider(new ScriptedRunner(_ => new ProcessResult(5, "", "Access denied", false)), NullLogger<WinGetProvider>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.RefreshSourcesAsync(TestContext.Current.CancellationToken));
        var timedOut = new WinGetProvider(new ScriptedRunner(_ => new ProcessResult(0, "partial output", "", true)), NullLogger<WinGetProvider>.Instance);
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut.GetSourcesAsync(TestContext.Current.CancellationToken));
    }
}
