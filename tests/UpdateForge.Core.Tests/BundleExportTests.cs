using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Bundles;
using UpdateForge.Core.Export;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Updates;
using UpdateForge.Providers.WinGet;
using Xunit;

namespace UpdateForge.Core.Tests;

public class CsvExportTests
{
    private static InstalledApplication App(string name, string? note = null) => new()
    {
        Id = "winget:A.B", Name = name, ProviderId = "winget", PackageId = "A.B", PackageSource = "winget",
        InstalledVersion = "1.0", AvailableVersion = "2.0", Status = UpdateStatus.UpdateAvailable, Support = UpdateSupport.AutoUpdatable, Note = note,
    };

    [Fact]
    public void Writes_header_and_one_row_per_application()
    {
        var lines = CsvExporter.Applications([App("Alpha"), App("Beta")]).TrimEnd().Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Name,Publisher,Installed,Available,Source,PackageId,Provider,Status,Support,Pinned,Excluded,Note", lines[0]);
        Assert.StartsWith("Alpha,,1.0,2.0,winget,A.B,winget,UpdateAvailable,AutoUpdatable,no,no", lines[1]);
    }

    [Fact]
    public void Quotes_commas_and_doubles_quotes()
    {
        var csv = CsvExporter.Applications([App("Acme, \"Pro\" Edition")]);
        Assert.Contains("\"Acme, \"\"Pro\"\" Edition\"", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(1)", "'=HYPERLINK(1)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Neutralizes_spreadsheet_formulas(string name, string expected)
    {
        var csv = CsvExporter.Applications([App(name)]);
        Assert.Contains("\r\n" + expected + ",", csv);
    }

    [Fact]
    public void Flattens_newlines_inside_a_cell() =>
        Assert.DoesNotContain("line one\nline two", CsvExporter.Applications([App("x", "line one\nline two")]));
}

public class BundleFormatTests
{
    private static string Json(string packages, int schema = 1) =>
        "{\"schemaVersion\":" + schema + ",\"createdAt\":\"2026-01-01T00:00:00Z\",\"createdBy\":\"t\",\"packages\":[" + packages + "]}";

    private static string Pkg(string id, string source = "winget", string? version = null) =>
        "{\"id\":\"" + id + "\",\"source\":\"" + source + "\"" + (version is null ? "" : ",\"version\":\"" + version + "\"") + "}";

    [Fact]
    public void Roundtrips_a_bundle()
    {
        var bundle = new PackageBundle(1, DateTimeOffset.UtcNow, "UpdateForge", [new("Mozilla.Firefox", "winget", "Firefox", "144.0"), new("XP123", "msstore")]);
        var parsed = PackageBundleFormat.Parse(PackageBundleFormat.Serialize(bundle));
        Assert.True(parsed.Ok);
        Assert.Equal(2, parsed.Bundle!.Packages.Count);
        Assert.Equal("Firefox", parsed.Bundle.Packages[0].Name);
        Assert.Empty(parsed.Rejected);
    }

    [Theory]
    [InlineData("Foo; calc.exe")]
    [InlineData("--force")]
    [InlineData("a b")]
    [InlineData("a/../b")]
    [InlineData("")]
    public void Rejects_dangerous_or_malformed_ids(string id)
    {
        var parsed = PackageBundleFormat.Parse(Json(Pkg(id)));
        Assert.True(parsed.Ok);
        Assert.Empty(parsed.Bundle!.Packages);
        Assert.Single(parsed.Rejected);
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("choco")]
    [InlineData("")]
    public void Rejects_sources_that_are_not_allowed(string source)
    {
        var parsed = PackageBundleFormat.Parse(Json(Pkg("Good.Pkg", source)));
        Assert.Empty(parsed.Bundle!.Packages);
    }

    [Fact]
    public void Rejects_bad_versions_and_duplicates_but_keeps_the_valid_entries()
    {
        var parsed = PackageBundleFormat.Parse(Json(string.Join(",", Pkg("A.One", "winget", "1.0 && whoami"), Pkg("A.Two", "WINGET", "2.0"), Pkg("a.two"))));
        var only = Assert.Single(parsed.Bundle!.Packages);
        Assert.Equal("A.Two", only.Id);
        Assert.Equal("winget", only.Source);
        Assert.Equal(2, parsed.Rejected.Count);
    }

    [Fact]
    public void Rejects_unsupported_schema_invalid_json_and_oversize_input()
    {
        Assert.Contains("Unsupported bundle version", PackageBundleFormat.Parse(Json("", schema: 9)).Error);
        Assert.False(PackageBundleFormat.Parse("{not json").Ok);
        Assert.False(PackageBundleFormat.Parse("").Ok);
        Assert.False(PackageBundleFormat.Parse(new string(' ', PackageBundleFormat.MaxBytes + 1)).Ok);
    }

    [Fact]
    public void Rejects_a_bundle_with_too_many_packages()
    {
        var many = string.Join(",", Enumerable.Range(0, PackageBundleFormat.MaxPackages + 1).Select(i => Pkg("P" + i)));
        Assert.Contains("more than", PackageBundleFormat.Parse(Json(many)).Error);
    }

    [Fact]
    public void Strips_control_characters_from_display_names()
    {
        var json = Json("{\"id\":\"A.B\",\"source\":\"winget\",\"name\":\"Nice\\u0007 Name\\u001b[31m\"}");
        var name = PackageBundleFormat.Parse(json).Bundle!.Packages[0].Name!;
        Assert.DoesNotContain(name, c => char.IsControl(c));
    }
}

public class BundleServiceTests : IDisposable
{
    private readonly EngineFixture _fx = new();
    private readonly FakeProvider _winget = new("winget");
    private BundleService Service() => new(new ProviderRegistry([_winget]), _fx.History, _fx.Lock, NullLogger<BundleService>.Instance);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static InstalledApplication App(string id, string provider = "winget", string source = "winget", string? version = "1.0", string name = "")
        => new() { Id = provider + ":" + id, Name = name.Length > 0 ? name : id, ProviderId = provider, PackageId = id, PackageSource = source, InstalledVersion = version };

    [Fact]
    public void Bundle_contains_only_winget_packages_once_each_without_unknown_versions()
    {
        var bundle = BundleService.CreateBundle(
        [
            App("B.Two", name: "Two"), App("A.One", version: "Unknown", name: "One"), App("A.One", name: "One again"),
            App("Reg.Entry", provider: "registry"), App("Bad Id!", name: "bad"), App("S.Pkg", source: "somewhere"),
        ]);
        Assert.Equal(["A.One", "B.Two"], bundle.Packages.Select(p => p.Id));
        Assert.Null(bundle.Packages[0].Version);
        Assert.Equal("1.0", bundle.Packages[1].Version);
    }

    [Fact]
    public void Plan_matches_installed_packages_case_insensitively()
    {
        var bundle = new PackageBundle(1, DateTimeOffset.UtcNow, "t", [new("Mozilla.Firefox", "winget"), new("New.Thing", "winget")]);
        var plan = BundleService.Plan(bundle, [App("mozilla.firefox")]);
        Assert.Equal(["New.Thing"], plan.ToInstall.Select(p => p.Id));
        Assert.Equal(["Mozilla.Firefox"], plan.AlreadyInstalled.Select(p => p.Id));
    }

    [Fact]
    public async Task Installs_only_missing_packages_and_records_each_in_history()
    {
        _winget.OnInstall = r => r.PackageId == "Bad.Pkg"
            ? UpdateResult.Fail(ErrorCategory.InstallerFailed, "boom", 1603)
            : new UpdateResult { Outcome = UpdateOutcome.Succeeded, Message = "Installed." };
        var plan = new BundlePlan([new("Good.Pkg", "winget", "Good"), new("Bad.Pkg", "winget", "Bad")], []);

        var results = await Service().InstallMissingAsync(plan, exactVersions: false, null, CancellationToken.None);

        Assert.Equal([JobState.Completed, JobState.Failed], results.Select(r => r.State));
        Assert.Equal(["Good.Pkg", "Bad.Pkg"], _winget.Installs.Select(i => i.PackageId));
        var hist = await _fx.History.QueryAsync("Good", 10, CancellationToken.None);
        Assert.Equal(JobState.Completed, hist.Single().FinalState);
        Assert.StartsWith("From bundle:", hist.Single().Message);
    }

    [Fact]
    public async Task Versions_are_only_requested_when_exact_versions_is_on()
    {
        var plan = new BundlePlan([new("V.Pkg", "winget", null, "3.2.1")], []);
        await Service().InstallMissingAsync(plan, exactVersions: false, null, CancellationToken.None);
        await Service().InstallMissingAsync(plan, exactVersions: true, null, CancellationToken.None);
        Assert.Equal(new string?[] { null, "3.2.1" }, _winget.Installs.Select(i => i.Version));
    }

    [Fact]
    public async Task Already_installed_result_is_skipped_not_failed()
    {
        _winget.OnInstall = _ => new UpdateResult { Outcome = UpdateOutcome.NoChange, Message = "Already installed." };
        var results = await Service().InstallMissingAsync(new BundlePlan([new("A.B", "winget")], []), false, null, CancellationToken.None);
        Assert.Equal(JobState.Skipped, results.Single().State);
    }

    [Fact]
    public async Task Cancel_stops_before_the_next_install()
    {
        using var cts = new CancellationTokenSource();
        _winget.OnInstall = _ => { cts.Cancel(); return new UpdateResult { Outcome = UpdateOutcome.Succeeded }; };
        var plan = new BundlePlan([new("A.One", "winget"), new("B.Two", "winget")], []);
        var results = await Service().InstallMissingAsync(plan, false, null, cts.Token);
        Assert.Equal([JobState.Completed, JobState.Canceled], results.Select(r => r.State));
        Assert.Single(_winget.Installs);
    }

    [Fact]
    public async Task Refuses_to_run_while_another_update_run_holds_the_lock()
    {
        _fx.Lock.Busy = true;
        await Assert.ThrowsAsync<EngineBusyException>(() => Service().InstallMissingAsync(new BundlePlan([new("A.B", "winget")], []), false, null, CancellationToken.None));
        Assert.Empty(_winget.Installs);
    }
}

public class WinGetInstallTests
{
    private static WinGetProvider Provider(ScriptedRunner runner) => new(runner, NullLogger<WinGetProvider>.Instance, "winget.exe");

    [Fact]
    public async Task Runs_install_with_exact_id_source_and_silent_flags()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "Successfully installed", "", false));
        var result = await Provider(runner).InstallAsync(new InstallRequest("Mozilla.Firefox", "winget", "144.0"), new Progress<UpdateProgress>(), CancellationToken.None);

        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal("Installed.", result.Message);
        var args = runner.Calls.Single().Arguments;
        Assert.Equal(["install", "--id", "Mozilla.Firefox", "--exact", "--source", "winget"], args.Take(6));
        Assert.Contains("--silent", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.Equal(["--version", "144.0"], args.TakeLast(2));
        Assert.DoesNotContain("--force", args);
    }

    [Theory]
    [InlineData("Foo; calc.exe", "winget", null)]
    [InlineData("--force", "winget", null)]
    [InlineData("Good.Pkg", "https://evil.example", null)]
    [InlineData("Good.Pkg", "winget", "1.0 && whoami")]
    public async Task Refuses_unvalidated_requests_without_starting_a_process(string id, string source, string? version)
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "", "", false));
        var result = await Provider(runner).InstallAsync(new InstallRequest(id, source, version), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Already_installed_exit_code_is_not_a_failure()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(unchecked((int)0x8A150061), "Found an existing package already installed.", "", false));
        var result = await Provider(runner).InstallAsync(new InstallRequest("A.B", "winget"), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.NoChange, result.Outcome);
    }
}
