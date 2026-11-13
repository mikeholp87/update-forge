using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;
using Xunit;

namespace UpdateForge.Core.Tests;

/// <summary>Provider that records how many updates were in flight at once.</summary>
internal sealed class ConcurrencyProbeProvider(string id) : IUpdateProvider
{
    private int _active;
    public int MaxActive;
    public string Id => id;
    public string DisplayName => "Probe";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, false);
    public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken ct) => Task.FromResult(new ProviderAvailability(ProviderHealth.Available, "1", null));
    public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledApplication>>([]);
    public async Task<UpdateResult> UpdateAsync(UpdateRequest r, IProgress<UpdateProgress> p, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _active);
        int seen;
        while ((seen = MaxActive) < now && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen) { }
        await Task.Delay(150, CancellationToken.None);
        Interlocked.Decrement(ref _active);
        return new UpdateResult { Outcome = UpdateOutcome.Succeeded };
    }
    public Task<string?> GetInstalledVersionAsync(InstalledApplication a, CancellationToken ct) => Task.FromResult<string?>("2.0");
}

public class ParallelEngineTests
{
    private static (UpdateEngine Engine, EngineFixture Fx) Build(ConcurrencyProbeProvider p)
    {
        var fx = new EngineFixture();
        return (new UpdateEngine(new ProviderRegistry([p]), fx.Jobs, fx.History, fx.Lock, NullLogger<UpdateEngine>.Instance), fx);
    }

    private static List<InstalledApplication> Apps(string provider, int n) =>
        [.. Enumerable.Range(0, n).Select(i => EngineFixture.App($"{provider}:P{i}") with { ProviderId = provider })];

    [Fact]
    public async Task Winget_updates_run_concurrently_up_to_the_limit_and_results_keep_order()
    {
        var probe = new ConcurrencyProbeProvider("winget");
        var (engine, fx) = Build(probe);
        using (fx)
        {
            var apps = Apps("winget", 6);
            var results = await engine.RunAsync(apps, EngineFixture.Policy, false, null, 3, CancellationToken.None);
            Assert.Equal(3, probe.MaxActive);
            Assert.All(results, r => Assert.Equal(JobState.Completed, r.FinalState));
            Assert.Equal(apps.Select(a => a.Id), results.Select(r => r.Application.Id));
        }
    }

    [Fact]
    public async Task Other_providers_stay_serial_even_when_parallelism_is_requested()
    {
        var probe = new ConcurrencyProbeProvider("chocolatey");
        var (engine, fx) = Build(probe);
        using (fx)
        {
            await engine.RunAsync(Apps("chocolatey", 4), EngineFixture.Policy, false, null, 3, CancellationToken.None);
            Assert.Equal(1, probe.MaxActive);
        }
    }

    [Fact]
    public async Task Default_overload_is_serial()
    {
        var probe = new ConcurrencyProbeProvider("winget");
        var (engine, fx) = Build(probe);
        using (fx)
        {
            await engine.RunAsync(Apps("winget", 3), EngineFixture.Policy, false, null, CancellationToken.None);
            Assert.Equal(1, probe.MaxActive);
        }
    }
}
