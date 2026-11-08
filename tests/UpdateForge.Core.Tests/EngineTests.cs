using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;
using UpdateForge.Infrastructure.Persistence;
using Xunit;

namespace UpdateForge.Core.Tests;

internal sealed class FakeProvider(string id = "fake") : IUpdateProvider
{
    public string Id => id;
    public string DisplayName => "Fake";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, false);
    public Func<UpdateRequest, UpdateResult> OnUpdate { get; set; } = _ => new UpdateResult { Outcome = UpdateOutcome.Succeeded };
    public string? VersionAfter { get; set; }
    public List<string> Updated { get; } = [];
    public bool ThrowOnDiscover { get; set; }
    public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken ct) => Task.FromResult(new ProviderAvailability(ProviderHealth.Available, "1", null));
    public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken ct) =>
        ThrowOnDiscover ? throw new InvalidOperationException("boom") : Task.FromResult<IReadOnlyList<InstalledApplication>>([]);
    public Task<UpdateResult> UpdateAsync(UpdateRequest r, IProgress<UpdateProgress> p, CancellationToken ct)
    {
        Updated.Add(r.Application.Id);
        return Task.FromResult(OnUpdate(r));
    }
    public Task<string?> GetInstalledVersionAsync(InstalledApplication a, CancellationToken ct) => Task.FromResult(VersionAfter);
}

internal sealed class TestLock : IEngineLock
{
    public bool Busy { get; set; }
    public IDisposable? TryAcquire() => Busy ? null : new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public sealed class EngineFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"uf-test-{Guid.NewGuid():N}.db");
    internal Database Db { get; }
    internal FakeProvider Provider { get; } = new();
    internal TestLock Lock { get; } = new();
    internal SqliteJobStore Jobs { get; }
    internal SqliteHistoryStore History { get; }
    internal UpdateEngine Engine { get; }

    public EngineFixture()
    {
        Db = new Database(_path);
        Db.Migrate();
        Jobs = new SqliteJobStore(Db);
        History = new SqliteHistoryStore(Db);
        Engine = new UpdateEngine(new ProviderRegistry([Provider]), Jobs, History, Lock, NullLogger<UpdateEngine>.Instance);
    }

    public static InstalledApplication App(string id = "fake:A", string installed = "1.0", string available = "2.0") => new()
    {
        Id = id, Name = id, ProviderId = "fake", PackageId = id, PackageSource = "winget", InstalledVersion = installed, AvailableVersion = available,
        Status = UpdateStatus.UpdateAvailable, Support = UpdateSupport.AutoUpdatable,
    };

    public static UpdatePolicySettings Policy => new() { Mode = AutomaticMode.AutomaticSafe };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }
}

public class EngineTests : IClassFixture<EngineFixture>
{
    private readonly EngineFixture _f;
    public EngineTests(EngineFixture f) => _f = f;

    [Fact]
    public async Task Success_with_changed_version_completes_and_is_recorded()
    {
        _f.Provider.VersionAfter = "2.0";
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:ok")], EngineFixture.Policy, false, null, CancellationToken.None);
        Assert.Equal(JobState.Completed, results.Single().FinalState);
        var hist = await _f.History.QueryAsync("fake:ok", 10, CancellationToken.None);
        Assert.Equal("2.0", hist.Single().NewVersion);
    }

    [Fact]
    public async Task Installer_success_without_version_change_is_a_failure()
    {
        _f.Provider.VersionAfter = "1.0"; // unchanged
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:nochange")], EngineFixture.Policy, false, null, CancellationToken.None);
        var r = results.Single();
        Assert.Equal(JobState.Failed, r.FinalState);
        Assert.Equal(ErrorCategory.VerificationFailed, r.Result!.Error);
    }

    [Fact]
    public async Task Restart_required_is_not_reported_as_completed()
    {
        _f.Provider.OnUpdate = _ => new UpdateResult { Outcome = UpdateOutcome.RestartRequired, RestartRequired = true };
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:reboot")], EngineFixture.Policy, false, null, CancellationToken.None);
        Assert.Equal(JobState.RestartRequired, results.Single().FinalState);
        _f.Provider.OnUpdate = _ => new UpdateResult { Outcome = UpdateOutcome.Succeeded };
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_others()
    {
        _f.Provider.VersionAfter = "9.0";
        _f.Provider.OnUpdate = r => r.Application.Id == "fake:bad" ? UpdateResult.Fail(ErrorCategory.InstallerFailed, "x") : new UpdateResult { Outcome = UpdateOutcome.Succeeded };
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:bad"), EngineFixture.App("fake:good")], EngineFixture.Policy, false, null, CancellationToken.None);
        Assert.Equal([JobState.Failed, JobState.Completed], results.Select(r => r.FinalState));
        _f.Provider.OnUpdate = _ => new UpdateResult { Outcome = UpdateOutcome.Succeeded };
    }

    [Fact]
    public async Task Provider_exceptions_become_failures()
    {
        _f.Provider.OnUpdate = _ => throw new InvalidOperationException("crash");
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:throw")], EngineFixture.Policy, false, null, CancellationToken.None);
        Assert.Equal(JobState.Failed, results.Single().FinalState);
        _f.Provider.OnUpdate = _ => new UpdateResult { Outcome = UpdateOutcome.Succeeded };
    }

    [Fact]
    public async Task Duplicate_requests_are_executed_once()
    {
        _f.Provider.Updated.Clear();
        _f.Provider.VersionAfter = "2.0";
        await _f.Engine.RunAsync([EngineFixture.App("fake:dup"), EngineFixture.App("fake:dup")], EngineFixture.Policy, false, null, CancellationToken.None);
        Assert.Single(_f.Provider.Updated, u => u == "fake:dup");
    }

    [Fact]
    public async Task Excluded_apps_are_never_passed_to_the_provider()
    {
        _f.Provider.Updated.Clear();
        var policy = EngineFixture.Policy with { ExcludedApplicationIds = new HashSet<string> { "fake:ex" } };
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:ex")], policy, false, null, CancellationToken.None);
        Assert.Equal(JobState.Skipped, results.Single().FinalState);
        Assert.Empty(_f.Provider.Updated);
    }

    [Fact]
    public async Task Soft_cancel_stops_queue_without_calling_providers()
    {
        _f.Provider.Updated.Clear();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:c1"), EngineFixture.App("fake:c2")], EngineFixture.Policy, false, null, cts.Token);
        Assert.All(results, r => Assert.Equal(JobState.Canceled, r.FinalState));
        Assert.Empty(_f.Provider.Updated);
    }

    [Fact]
    public async Task Concurrent_engine_run_is_refused()
    {
        _f.Lock.Busy = true;
        try { await Assert.ThrowsAsync<EngineBusyException>(() => _f.Engine.RunAsync([EngineFixture.App()], EngineFixture.Policy, false, null, CancellationToken.None)); }
        finally { _f.Lock.Busy = false; }
    }

    [Fact]
    public async Task Interrupted_jobs_are_recovered_as_failed_never_completed()
    {
        var id = await _f.Jobs.CreateItemAsync(Guid.NewGuid(), EngineFixture.App("fake:crashed"), JobState.Installing, CancellationToken.None);
        var recovered = await _f.Jobs.RecoverInterruptedAsync(CancellationToken.None);
        Assert.Contains(recovered, r => r.Id == id);
        var recent = await _f.Jobs.GetRecentAsync(50, CancellationToken.None);
        Assert.Equal(JobState.Failed, recent.Single(r => r.Id == id).State);
    }

    [Fact]
    public async Task Unattended_manual_mode_never_installs()
    {
        _f.Provider.Updated.Clear();
        var results = await _f.Engine.RunAsync([EngineFixture.App("fake:auto")], new UpdatePolicySettings { Mode = AutomaticMode.Manual }, true, null, CancellationToken.None);
        Assert.Equal(JobState.Skipped, results.Single().FinalState);
        Assert.Empty(_f.Provider.Updated);
    }
}

public class DatabaseTests
{
    [Fact]
    public void Migration_is_idempotent_and_sets_schema_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uf-mig-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            db.Migrate();
            using var c = db.Open();
            Assert.Equal((long)Database.Migrations.Count, (long)Database.Scalar(c, null, "PRAGMA user_version;")!);
            Assert.Equal(1L, (long)Database.Scalar(c, null, "SELECT COUNT(*) FROM sqlite_master WHERE name='UpdateHistory';")!);
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public void Newer_schema_is_refused_not_modified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uf-new-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            using (var c = db.Open()) Database.Exec(c, null, "PRAGMA user_version = 999;");
            Assert.Throws<InvalidOperationException>(db.Migrate);
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public async Task Settings_roundtrip_and_corrupt_value_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uf-set-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            var store = new SqliteSettingsStore(db);
            await store.SaveAsync(new AppSettings { Mode = AutomaticMode.AutomaticSafe, Theme = "Dark" }, CancellationToken.None);
            Assert.Equal("Dark", (await store.LoadAsync(CancellationToken.None)).Theme);
            using (var c = db.Open()) Database.Exec(c, null, "UPDATE Settings SET Value='{not json' WHERE Key='app';");
            Assert.Equal(AutomaticMode.Manual, (await store.LoadAsync(CancellationToken.None)).Mode);
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public async Task History_search_treats_wildcards_literally()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uf-hist-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            var h = new SqliteHistoryStore(db);
            await h.AddAsync(new HistoryRecord(0, DateTimeOffset.UtcNow, "a:1", "Alpha", "p", "1", "2", "2", JobState.Completed, TimeSpan.FromSeconds(1), 0, ErrorCategory.None, false, null, null), CancellationToken.None);
            Assert.Empty(await h.QueryAsync("%", 10, CancellationToken.None));
            Assert.Single(await h.QueryAsync("alph", 10, CancellationToken.None));
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { } }
    }
}
