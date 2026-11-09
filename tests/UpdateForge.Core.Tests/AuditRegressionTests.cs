using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Processes;
using UpdateForge.Infrastructure.Diagnostics;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using Xunit;

namespace UpdateForge.Core.Tests;

/// <summary>Regressions found in the full audit: a failed query must never read as "everything is up to date".</summary>
public class AuditRegressionTests
{
    private static ProcessResult Timeout() => new(-1, "", "", true);
    private static ProcessResult Failed() => new(1, "", "boom", false);
    private static ProcessResult Ok(string text) => new(0, text, "", false);

    [Fact]
    public async Task Scoop_export_timeout_is_an_error_not_an_empty_inventory()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var p = new ScoopProvider(new ScriptedRunner(_ => Timeout()), NullLogger<ScoopProvider>.Instance, exe, exe);
            await Assert.ThrowsAsync<InvalidOperationException>(() => p.DiscoverAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Scoop_status_timeout_is_an_error_not_up_to_date()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(s => s.Arguments.Contains("export") ? Ok("""{"apps":[{"Info":"","Name":"7zip","Source":"main","Version":"23.01"}]}""") : Timeout());
            var p = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, exe, exe);
            await Assert.ThrowsAsync<TimeoutException>(() => p.DiscoverAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Chocolatey_list_failure_is_an_error()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var p = new ChocolateyProvider(new ScriptedRunner(_ => Failed()), NullLogger<ChocolateyProvider>.Instance, exe, () => true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => p.DiscoverAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Chocolatey_outdated_timeout_is_an_error_not_up_to_date()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(s => s.Arguments.Contains("list") ? Ok("git|2.40.0") : Timeout());
            var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, exe, () => true);
            await Assert.ThrowsAsync<TimeoutException>(() => p.DiscoverAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Missing_or_unstartable_executable_reports_unavailable_instead_of_throwing()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var throwing = new ThrowingRunner();
            var scoop = new ScoopProvider(throwing, NullLogger<ScoopProvider>.Instance, exe, exe);
            var choco = new ChocolateyProvider(throwing, NullLogger<ChocolateyProvider>.Instance, exe, () => false);
            Assert.False((await scoop.CheckAvailabilityAsync(TestContext.Current.CancellationToken)).IsAvailable);
            Assert.False((await choco.CheckAvailabilityAsync(TestContext.Current.CancellationToken)).IsAvailable);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Chocolatey_outdated_does_not_pass_an_unverified_ignore_pinned_flag()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(s => s.Arguments.Contains("list") ? Ok("git|2.40.0") : Ok(""));
            var p = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, exe, () => true);
            await p.DiscoverAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(runner.Calls, c => c.Arguments.Any(a => a.StartsWith("--ignore-pinned", StringComparison.Ordinal)));
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Diagnostics_export_refuses_to_delete_a_non_zip_file()
    {
        var victim = Path.Combine(Path.GetTempPath(), $"uf-keep-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(victim, "precious", TestContext.Current.CancellationToken);
        try
        {
            var exporter = new DiagnosticsExporter(null!, null!, null!, null!);
            await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(victim, Path.GetTempPath(), TestContext.Current.CancellationToken));
            Assert.Equal("precious", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(victim); }
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            throw new System.ComponentModel.Win32Exception("not startable");
    }
}
