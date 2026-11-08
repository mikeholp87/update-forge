using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using Xunit;

namespace UpdateForge.Core.Tests;

internal sealed class ScriptedRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Calls.Add(spec);
        return Task.FromResult(respond(spec));
    }
}

public class ScoopTests
{
    private const string Export = """
        {"buckets":[{"Name":"main"}],"apps":[
          {"Info":"","Name":"7zip","Source":"main","Updated":"2025-01-01","Version":"23.01"},
          {"Info":"Held package","Name":"git","Source":"main","Updated":"2025-01-01","Version":"2.40.0"},
          {"Info":"Global install","Name":"nodejs","Source":"main","Updated":"2025-01-01","Version":"20.0.0"}]}
        """;

    private const string Status = """
        Scoop is up to date.
        Name   Installed Version Latest Version Missing Dependencies Info
        ----   ----------------- -------------- -------------------- ----
        7zip   23.01             24.09
        git    2.40.0            2.45.0                              Held package
        """;

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    [Fact]
    public void Export_parsing_detects_held_and_global()
    {
        var apps = ScoopParsing.ParseExport(Export);
        Assert.Equal(3, apps.Count);
        Assert.True(apps.Single(a => a.Name == "git").Held);
        Assert.True(apps.Single(a => a.Name == "nodejs").Global);
    }

    [Fact]
    public async Task Held_global_and_updatable_are_classified_correctly()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(s => s.Arguments.Contains("export") ? Ok(Export) : s.Arguments.Contains("status") ? Ok(Status) : Ok("1.0"));
            var p = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, exe, exe);
            var apps = await p.DiscoverAsync(CancellationToken.None);
            var zip = apps.Single(a => a.PackageId == "7zip");
            Assert.True(zip.UpdateAvailable);
            Assert.Equal("24.09", zip.AvailableVersion);
            Assert.True(zip.CanAutoUpdate);
            var git = apps.Single(a => a.PackageId == "git");
            Assert.True(git.IsPinned);
            Assert.False(git.CanAutoUpdate);
            var node = apps.Single(a => a.PackageId == "nodejs");
            Assert.True(node.RequiresElevation);
            Assert.Equal(UpdateSupport.ApprovalRequired, node.Support);
        }
        finally { File.Delete(exe); }
    }

    [Theory]
    [InlineData("7zip; calc")]
    [InlineData("--force")]
    [InlineData("a b")]
    [InlineData("..\\x")]
    public async Task Malicious_package_names_are_never_executed(string name)
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var p = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "x", "y");
        var app = new InstalledApplication { Id = "scoop:x", Name = "x", ProviderId = "scoop", PackageId = name };
        var r = await p.UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, r.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Held_packages_are_not_updated()
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var p = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "x", "y");
        var app = new InstalledApplication { Id = "scoop:git", Name = "git", ProviderId = "scoop", PackageId = "git", IsPinned = true };
        var r = await p.UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Blocked, r.Outcome);
        Assert.Empty(runner.Calls);
    }
}

public class ChocolateyTests
{
    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static InstalledApplication App(string id, bool pinned = false) =>
        new() { Id = $"chocolatey:{id}", Name = id, ProviderId = "chocolatey", PackageId = id, IsPinned = pinned };

    [Fact]
    public void Parses_limit_output()
    {
        Assert.Equal(2, ChocolateyParsing.ParseList("git|2.40.0\nvlc|3.0.20\nChocolatey v2.2.2\n").Count);
        var o = ChocolateyParsing.ParseOutdated("git|2.40.0|2.45.0|false\nvlc|3.0.20|3.0.21|true\n");
        Assert.True(o.Single(x => x.Id == "vlc").Pinned);
    }

    [Fact]
    public async Task Unelevated_discovery_marks_updates_as_requiring_approval()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(s => s.Arguments.Contains("list") ? Ok("git|2.40.0\nvlc|3.0.20\n") : Ok("git|2.40.0|2.45.0|false\nvlc|3.0.20|3.0.21|true\n"));
            var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, exe, () => false);
            var apps = await p.DiscoverAsync(CancellationToken.None);
            var git = apps.Single(a => a.PackageId == "git");
            Assert.True(git.UpdateAvailable);
            Assert.False(git.CanAutoUpdate);
            Assert.True(git.RequiresElevation);
            Assert.True(apps.Single(a => a.PackageId == "vlc").IsPinned);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Unelevated_upgrade_is_not_attempted()
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "x", () => false);
        var r = await p.UpdateAsync(new UpdateRequest(App("git")), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.ManualActionRequired, r.Outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Elevated_upgrade_never_uses_force_or_checksum_overrides()
    {
        var runner = new ScriptedRunner(_ => Ok("ok"));
        var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "x", () => true);
        var r = await p.UpdateAsync(new UpdateRequest(App("git")), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Succeeded, r.Outcome);
        var args = runner.Calls.Single().Arguments;
        Assert.DoesNotContain(args, a => a.Contains("force", StringComparison.OrdinalIgnoreCase) || a.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("git & calc")]
    [InlineData("git;calc")]
    [InlineData("-y")]
    [InlineData("a|b")]
    public async Task Malicious_ids_are_never_executed(string id)
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "x", () => true);
        var r = await p.UpdateAsync(new UpdateRequest(App(id)), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, r.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Pinned_packages_are_blocked()
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "x", () => true);
        var r = await p.UpdateAsync(new UpdateRequest(App("git", pinned: true)), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Blocked, r.Outcome);
        Assert.Empty(runner.Calls);
    }
}
