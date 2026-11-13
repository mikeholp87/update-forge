using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Providers.Npm;
using Xunit;

namespace UpdateForge.Core.Tests;

public class NpmParsingTests
{
    // Captured from a real `npm ls -g --depth=0 --json`.
    private const string RealList = """
        {
          "name": "npm",
          "dependencies": {
            "@opencode/cli": { "version": "2.3.0", "overridden": false },
            "vercel": { "version": "65.15.0", "overridden": false }
          }
        }
        """;

    [Fact]
    public void Parses_global_packages_including_scoped_names()
    {
        var list = NpmParsing.ParseList(RealList);
        Assert.Equal(["@opencode/cli", "vercel"], list.Select(p => p.Name));
        Assert.Equal("65.15.0", list[1].Version);
        Assert.All(list, p => Assert.False(p.Linked));
    }

    [Fact]
    public void Skips_entries_without_a_version_and_flags_linked_packages()
    {
        const string json = """
            {"dependencies":{
              "broken":{"missing":true},
              "mine":{"version":"1.0.0","resolved":"file:..\\..\\work\\mine"},
              "ok":{"version":"3.1.4"}}}
            """;
        var list = NpmParsing.ParseList(json);
        Assert.Equal(["mine", "ok"], list.Select(p => p.Name));
        Assert.True(list[0].Linked);
        Assert.False(list[1].Linked);
    }

    [Fact]
    public void An_empty_install_is_an_empty_list_not_an_error() => Assert.Empty(NpmParsing.ParseList("{}"));

    [Fact]
    public void Output_that_is_not_json_is_an_error_not_an_empty_list() =>
        Assert.Throws<InvalidOperationException>(() => NpmParsing.ParseList("npm error something went wrong"));

    [Fact]
    public void Tolerates_a_notice_printed_before_the_json() =>
        Assert.Single(NpmParsing.ParseList("npm notice hello\n{\"dependencies\":{\"a\":{\"version\":\"1.0.0\"}}}"));

    [Fact]
    public void Outdated_maps_names_to_latest_and_nothing_outdated_is_empty()
    {
        const string json = """
            {"vercel":{"current":"65.12.1","wanted":"65.15.0","latest":"65.15.0","dependent":"global","location":"x"},
             "multi":[{"current":"1.0.0","wanted":"1.0.0","latest":"2.0.0"}]}
            """;
        var latest = NpmParsing.ParseOutdated(json);
        Assert.Equal("65.15.0", latest["vercel"]);
        Assert.Equal("2.0.0", latest["multi"]);
        Assert.Empty(NpmParsing.ParseOutdated("{}"));
        Assert.Empty(NpmParsing.ParseOutdated(""));
    }

    [Fact]
    public void Search_parses_real_output_shape_and_drops_invalid_names()
    {
        const string json = """
            [

            {"name":"rimraf","keywords":["rm"],"version":"6.1.4","description":"A deep deletion module","publisher":{"username":"isaacs"}}

            ,

            {"name":"@zkochan/rimraf","version":"4.0.0"}

            ,

            {"name":"evil; calc","version":"1.0.0"},
            {"name":"--force","version":"1.0.0"},
            {"name":"noversion"}
            ]
            """;
        var found = NpmParsing.ParseSearch(json, n => System.Text.RegularExpressions.Regex.IsMatch(n, "^(@[a-z0-9-]+/)?[a-z0-9][a-z0-9-]*$"));
        Assert.Equal(["rimraf", "@zkochan/rimraf", "noversion"], found.Select(p => p.PackageId));
        Assert.Equal("6.1.4", found[0].Version);
        Assert.Equal("Unknown", found[2].Version);
        Assert.All(found, p => Assert.Equal("npm", p.ProviderId));
    }
}

public sealed class NpmProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uf-npm-").FullName;
    private readonly string _node;
    private readonly string _cli;

    public NpmProviderTests()
    {
        _node = Path.Combine(_dir, "node.exe");
        _cli = Path.Combine(_dir, "npm-cli.js");
        File.WriteAllText(_node, "");
        File.WriteAllText(_cli, "");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private NpmProvider Provider(ScriptedRunner runner, bool elevated = false) =>
        new(runner, NullLogger<NpmProvider>.Instance, _node, _cli, () => elevated);

    private static ProcessResult Ok(string text = "") => new(0, text, "", false);

    private static InstalledApplication App(string name = "vercel", string? available = "65.15.0", bool pinned = false) => new()
    {
        Id = "npm:" + name, Name = name, ProviderId = "npm", PackageId = name, InstalledVersion = "65.12.1", AvailableVersion = available, IsPinned = pinned,
    };

    private static string Cmd(ProcessSpec s) => s.Arguments[1];

    [Fact]
    public async Task Runs_node_directly_with_the_npm_script_never_a_shell_or_cmd_file()
    {
        var runner = new ScriptedRunner(_ => Ok("11.20.0"));
        await Provider(runner).CheckAvailabilityAsync(CancellationToken.None);

        var spec = runner.Calls.Single();
        Assert.Equal(_node, spec.FileName);
        Assert.Equal(_cli, spec.Arguments[0]);
        Assert.DoesNotContain(spec.Arguments, a => a.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--no-audit", spec.Arguments);
    }

    [Fact]
    public async Task Reports_not_installed_when_node_or_npm_cannot_be_found()
    {
        var missing = new NpmProvider(new ScriptedRunner(_ => Ok()), NullLogger<NpmProvider>.Instance, Path.Combine(_dir, "no.exe"), Path.Combine(_dir, "no.js"));
        var availability = await missing.CheckAvailabilityAsync(CancellationToken.None);
        Assert.Equal(ProviderHealth.NotInstalled, availability.Health);
    }

    [Fact]
    public async Task Discovers_packages_and_marks_the_outdated_ones()
    {
        var runner = new ScriptedRunner(s => Cmd(s) switch
        {
            "ls" => Ok("{\"dependencies\":{\"vercel\":{\"version\":\"65.12.1\"},\"@opencode/cli\":{\"version\":\"2.3.0\"}}}"),
            // npm outdated exits 1 when something is outdated: that is success, not failure.
            "outdated" => new ProcessResult(1, "{\"vercel\":{\"current\":\"65.12.1\",\"wanted\":\"65.15.0\",\"latest\":\"65.15.0\"}}", "", false),
            _ => Ok(),
        });
        var apps = await Provider(runner).DiscoverAsync(CancellationToken.None);

        var vercel = apps.Single(a => a.PackageId == "vercel");
        Assert.Equal(UpdateStatus.UpdateAvailable, vercel.Status);
        Assert.Equal("65.15.0", vercel.AvailableVersion);
        Assert.Equal("npm:vercel", vercel.Id);
        var scoped = apps.Single(a => a.PackageId == "@opencode/cli");
        Assert.Equal(UpdateStatus.UpToDate, scoped.Status);
        Assert.Null(scoped.AvailableVersion);
    }

    [Fact]
    public async Task A_linked_package_is_pinned_and_never_offered_an_update()
    {
        var runner = new ScriptedRunner(s => Cmd(s) switch
        {
            "ls" => Ok("{\"dependencies\":{\"mine\":{\"version\":\"1.0.0\",\"resolved\":\"file:..\\\\mine\"}}}"),
            "outdated" => new ProcessResult(1, "{\"mine\":{\"current\":\"1.0.0\",\"latest\":\"9.0.0\"}}", "", false),
            _ => Ok(),
        });
        var app = Assert.Single(await Provider(runner).DiscoverAsync(CancellationToken.None));
        Assert.True(app.IsPinned);
        Assert.False(app.UpdateAvailable);
    }

    [Fact]
    public async Task A_failed_outdated_check_is_a_provider_error_not_everything_up_to_date()
    {
        var runner = new ScriptedRunner(s => Cmd(s) switch
        {
            "ls" => Ok("{\"dependencies\":{\"vercel\":{\"version\":\"1.0.0\"}}}"),
            _ => new ProcessResult(1, "", "npm error code ENOTFOUND", false),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_timed_out_listing_is_a_provider_error()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(-1, "", "", true));
        await Assert.ThrowsAsync<TimeoutException>(() => Provider(runner).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_installs_the_exact_version_that_was_checked()
    {
        var runner = new ScriptedRunner(_ => Ok());
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(App()), new Progress<UpdateProgress>(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal(["install", "-g", "vercel@65.15.0"], runner.Calls.Single().Arguments.Skip(1).Take(3));
    }

    [Fact]
    public async Task Update_handles_scoped_names()
    {
        var runner = new ScriptedRunner(_ => Ok());
        await Provider(runner).UpdateAsync(new UpdateRequest(App("@opencode/cli", "2.4.0")), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal("@opencode/cli@2.4.0", runner.Calls.Single().Arguments[3]);
    }

    [Theory]
    [InlineData("vercel; calc", "1.0.0")]
    [InlineData("--global", "1.0.0")]
    [InlineData("a b", "1.0.0")]
    [InlineData("vercel", "1.0.0 && whoami")]
    [InlineData("vercel", "latest; rm")]
    public async Task Update_refuses_unvalidated_names_and_versions_without_starting_a_process(string name, string version)
    {
        var runner = new ScriptedRunner(_ => Ok());
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(App(name, version)), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Update_never_replaces_a_linked_package()
    {
        var runner = new ScriptedRunner(_ => Ok());
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(App(pinned: true)), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Blocked, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("npm error code EACCES\nnpm error permission denied", false, UpdateOutcome.ManualActionRequired, ErrorCategory.ElevationRequired)]
    [InlineData("npm error code EPERM", true, UpdateOutcome.Failed, ErrorCategory.InstallerFailed)]
    [InlineData("npm error code EBUSY", false, UpdateOutcome.Failed, ErrorCategory.ApplicationInUse)]
    [InlineData("npm error code ENOTFOUND registry.npmjs.org", false, UpdateOutcome.Failed, ErrorCategory.Network)]
    [InlineData("npm error something else", false, UpdateOutcome.Failed, ErrorCategory.InstallerFailed)]
    public async Task Failures_are_classified_so_the_user_sees_the_real_reason(string stderr, bool elevated, UpdateOutcome outcome, ErrorCategory error)
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(1, "", stderr, false));
        var result = await Provider(runner, elevated).UpdateAsync(new UpdateRequest(App()), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal((outcome, error), (result.Outcome, result.Error));
        Assert.Contains("npm error", result.TechnicalDetails);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_a_timeout()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(-1, "", "", true));
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(App()), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.Timeout, result.Error);
    }

    [Fact]
    public async Task Install_uses_the_validated_name_and_optional_version()
    {
        var runner = new ScriptedRunner(_ => Ok());
        var p = Provider(runner);
        await p.InstallAsync(new InstallRequest("rimraf", null), new Progress<UpdateProgress>(), CancellationToken.None);
        await p.InstallAsync(new InstallRequest("@zkochan/rimraf", "winget", "4.0.0"), new Progress<UpdateProgress>(), CancellationToken.None);

        Assert.Equal("rimraf", runner.Calls[0].Arguments[3]);
        Assert.Equal("@zkochan/rimraf@4.0.0", runner.Calls[1].Arguments[3]);
    }

    [Theory]
    [InlineData("evil; calc", null)]
    [InlineData("--prefix=C:\\", null)]
    [InlineData("rimraf", "1 && calc")]
    public async Task Install_refuses_unvalidated_requests_without_starting_a_process(string name, string? version)
    {
        var runner = new ScriptedRunner(_ => Ok());
        var result = await Provider(runner).InstallAsync(new InstallRequest(name, null, version), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Search_passes_the_query_as_a_single_argument_and_parses_results()
    {
        var runner = new ScriptedRunner(_ => Ok("[{\"name\":\"rimraf\",\"version\":\"6.1.4\"}]"));
        var found = await Provider(runner).SearchPackagesAsync("rm -rf", CancellationToken.None);

        Assert.Equal("rimraf", Assert.Single(found).PackageId);
        var args = runner.Calls.Single().Arguments;
        Assert.Equal("search", args[1]);
        Assert.Equal("rm -rf", args[2]);
        Assert.Contains("--json", args);
    }

    [Fact]
    public async Task Search_rejects_option_like_queries_and_reports_failures()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(1, "", "npm error network", false));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(runner).SearchPackagesAsync("--registry=http://evil", CancellationToken.None));
        Assert.Empty(runner.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).SearchPackagesAsync("rimraf", CancellationToken.None));
    }

    [Fact]
    public async Task Verifies_the_installed_version_after_an_update()
    {
        var runner = new ScriptedRunner(_ => Ok("{\"dependencies\":{\"vercel\":{\"version\":\"65.15.0\"}}}"));
        Assert.Equal("65.15.0", await Provider(runner).GetInstalledVersionAsync(App(), CancellationToken.None));
        Assert.Equal(["ls", "-g", "vercel"], runner.Calls.Single().Arguments.Skip(1).Take(3));
    }

    [Fact]
    public async Task Shows_the_registry_as_the_source_and_has_nothing_to_refresh()
    {
        var runner = new ScriptedRunner(_ => Ok("https://registry.npmjs.org/"));
        var p = Provider(runner);
        Assert.Contains("https://registry.npmjs.org/", await p.GetSourcesAsync(CancellationToken.None));
        Assert.False(p.CanRefreshSources);
        await Assert.ThrowsAsync<NotSupportedException>(() => p.RefreshSourcesAsync(CancellationToken.None));
    }
}
