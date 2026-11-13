using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using Xunit;

namespace UpdateForge.Core.Tests;

public sealed class PackageInstallServiceTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly FakeProvider _provider = new("winget");
    private readonly InstallSettings _settings = new("winget");

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private PackageInstallService Service() =>
        new(new ProviderRegistry([_provider]), _settings, _fx.History, _fx.Lock, NullLogger<PackageInstallService>.Instance);

    private static PackageSearchResult Package(string provider = "winget", string id = "Git.Git", string? source = "winget") =>
        new(provider, id, "Git", "2.0", source);

    [Fact]
    public async Task Installs_the_chosen_package_and_records_it_in_history()
    {
        var outcome = await Service().InstallAsync(Package(), null, CancellationToken.None);

        Assert.True(outcome.Installed);
        var request = Assert.Single(_provider.Installs);
        Assert.Equal(("Git.Git", "winget", null), (request.PackageId, request.Source, request.Version));
        var history = Assert.Single(await _fx.History.QueryAsync("Git", 10, CancellationToken.None));
        Assert.Equal(JobState.Completed, history.FinalState);
        Assert.StartsWith("From Discover:", history.Message);
    }

    [Fact]
    public async Task Refuses_a_manager_that_is_not_enabled_without_calling_it()
    {
        _settings.Value = new AppSettings { EnabledProviders = [] };
        var outcome = await Service().InstallAsync(Package(), null, CancellationToken.None);

        Assert.Equal(JobState.Blocked, outcome.State);
        Assert.Empty(_provider.Installs);
    }

    [Fact]
    public async Task Unknown_manager_is_blocked()
    {
        var outcome = await Service().InstallAsync(Package(provider: "nope"), null, CancellationToken.None);
        Assert.Equal(JobState.Blocked, outcome.State);
        Assert.Empty(_provider.Installs);
    }

    [Fact]
    public async Task Failures_are_reported_and_recorded_with_the_exit_code()
    {
        _provider.OnInstall = _ => UpdateResult.Fail(ErrorCategory.InstallerFailed, "boom", 1603);
        var outcome = await Service().InstallAsync(Package(), null, CancellationToken.None);

        Assert.False(outcome.Installed);
        Assert.Equal(JobState.Failed, outcome.State);
        Assert.Equal(1603, Assert.Single(await _fx.History.QueryAsync("Git", 10, CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task A_provider_exception_becomes_a_failed_install_not_a_crash()
    {
        _provider.OnInstall = _ => throw new InvalidOperationException("provider blew up");
        var outcome = await Service().InstallAsync(Package(), null, CancellationToken.None);
        Assert.Equal(JobState.Failed, outcome.State);
        Assert.Contains("provider blew up", outcome.Message);
    }

    [Fact]
    public async Task Restart_required_counts_as_installed_and_is_flagged()
    {
        _provider.OnInstall = _ => new UpdateResult { Outcome = UpdateOutcome.RestartRequired, RestartRequired = true, Message = "Installed; restart." };
        var outcome = await Service().InstallAsync(Package(), null, CancellationToken.None);
        Assert.True(outcome.Installed);
        Assert.True(outcome.RestartRequired);
    }

    [Fact]
    public async Task Refuses_to_run_while_an_update_run_holds_the_lock()
    {
        _fx.Lock.Busy = true;
        await Assert.ThrowsAsync<EngineBusyException>(() => Service().InstallAsync(Package(), null, CancellationToken.None));
        Assert.Empty(_provider.Installs);
    }

    private sealed class InstallSettings(params string[] enabled) : ISettingsStore
    {
        public AppSettings Value { get; set; } = new() { EnabledProviders = enabled };
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }
}

public class ScoopInstallTests
{
    private static ProcessResult Ok(string text = "'git' (2.45.0) was installed successfully!") => new(0, text, "", false);
    private static ScoopProvider Provider(ScriptedRunner runner) => new(runner, NullLogger<ScoopProvider>.Instance, "scoop.ps1", "powershell.exe");
    private static Task<UpdateResult> Install(ScopedRunner r, InstallRequest q) => r.Provider.InstallAsync(q, new Progress<UpdateProgress>(), CancellationToken.None);

    private sealed class ScopedRunner
    {
        public ScriptedRunner Runner { get; }
        public ScoopProvider Provider { get; }
        public ScopedRunner(Func<ProcessSpec, ProcessResult> respond) { Runner = new ScriptedRunner(respond); Provider = ScoopInstallTests.Provider(Runner); }
    }

    [Fact]
    public async Task Installs_from_the_named_bucket()
    {
        var s = new ScopedRunner(_ => Ok());
        var result = await Install(s, new InstallRequest("vscode", "extras"));

        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal(["install", "extras/vscode"], s.Runner.Calls.Single().Arguments.TakeLast(2));
    }

    [Fact]
    public async Task Installs_by_name_when_no_bucket_is_known()
    {
        var s = new ScopedRunner(_ => Ok());
        await Install(s, new InstallRequest("git", null));
        Assert.Equal(["install", "git"], s.Runner.Calls.Single().Arguments.TakeLast(2));
    }

    [Theory]
    [InlineData("git; calc", "main")]
    [InlineData("--global", "main")]
    [InlineData("git", "https://evil.example/bucket")]
    [InlineData("git", "a b")]
    public async Task Refuses_unvalidated_names_and_buckets_without_starting_a_process(string name, string bucket)
    {
        var s = new ScopedRunner(_ => Ok());
        var result = await Install(s, new InstallRequest(name, bucket));
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(s.Runner.Calls);
    }

    [Fact]
    public async Task A_specific_version_is_not_supported_and_nothing_runs()
    {
        var s = new ScopedRunner(_ => Ok());
        var result = await Install(s, new InstallRequest("git", "main", "2.0"));
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Empty(s.Runner.Calls);
    }

    [Fact]
    public async Task Already_installed_is_not_reported_as_a_fresh_install()
    {
        var s = new ScopedRunner(_ => Ok("WARN  'git' (2.45.0) is already installed."));
        Assert.Equal(UpdateOutcome.NoChange, (await Install(s, new InstallRequest("git", "main"))).Outcome);
    }

    [Fact]
    public async Task A_nonzero_exit_code_is_a_failure_with_details()
    {
        var s = new ScopedRunner(_ => new ProcessResult(1, "Couldn't find manifest", "", false));
        var result = await Install(s, new InstallRequest("git", "main"));
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("manifest", result.TechnicalDetails);
    }
}

public class ChocolateyInstallTests
{
    private static ChocolateyProvider Provider(ScriptedRunner runner, bool elevated = true) =>
        new(runner, NullLogger<ChocolateyProvider>.Instance, "choco.exe", () => elevated);

    private static Task<UpdateResult> Install(ChocolateyProvider p, InstallRequest q) => p.InstallAsync(q, new Progress<UpdateProgress>(), CancellationToken.None);

    [Fact]
    public async Task Installs_with_safe_flags_and_never_weakens_integrity_checks()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "git|2.45.0", "", false));
        var result = await Install(Provider(runner), new InstallRequest("git", null));

        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        var args = runner.Calls.Single().Arguments;
        Assert.Equal(["install", "git"], args.Take(2));
        Assert.Contains("--yes", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--ignore-checksums", args);
        Assert.DoesNotContain("--allow-empty-checksums", args);
    }

    [Fact]
    public async Task Without_administrator_rights_nothing_is_run_and_the_user_is_told_why()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "", "", false));
        var result = await Install(Provider(runner, elevated: false), new InstallRequest("git", null));

        Assert.Equal(UpdateOutcome.ManualActionRequired, result.Outcome);
        Assert.Equal(ErrorCategory.ElevationRequired, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("git; calc", null)]
    [InlineData("--force", null)]
    [InlineData("git", "1.0 && whoami")]
    public async Task Refuses_unvalidated_requests_without_starting_a_process(string id, string? version)
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "", "", false));
        var result = await Install(Provider(runner), new InstallRequest(id, null, version));
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Exit_code_3010_means_installed_but_restart_required()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(3010, "", "", false));
        var result = await Install(Provider(runner), new InstallRequest("git", null));
        Assert.Equal(UpdateOutcome.RestartRequired, result.Outcome);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task A_requested_version_is_passed_through_validated()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "", "", false));
        await Install(Provider(runner), new InstallRequest("git", null, "2.45.0"));
        Assert.Equal(["--version", "2.45.0"], runner.Calls.Single().Arguments.TakeLast(2));
    }
}
