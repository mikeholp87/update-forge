using System.Diagnostics;
using UpdateForge.Core.Processes;
using UpdateForge.Providers.Linux;
using Xunit;

namespace UpdateForge.Core.Tests;

public class AptRootSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uf-session-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A stand-in for the helper: says R, then answers every request line with its text and exit code 0 (7 for "fail").</summary>
    private string Fake(string body)
    {
        var path = Path.Combine(_dir, "helper.sh");
        File.WriteAllText(path, "#!/bin/bash\necho start >> \"" + Path.Combine(_dir, "starts") + "\"\n" + body);
        return path;
    }

    private int Starts => File.Exists(Path.Combine(_dir, "starts")) ? File.ReadAllLines(Path.Combine(_dir, "starts")).Length : 0;

    private const string Echo = "echo R\nwhile read -r id verb arg; do echo \"O $id got $verb $arg\"; [ \"$verb\" = fail ] && echo \"X $id 7\" || echo \"X $id 0\"; done\n";

    [Fact]
    public async Task One_helper_serves_every_request_so_authorization_is_asked_once()
    {
        using var s = new AptRootSession(["/bin/bash", Fake(Echo)]);
        var lines = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var r = await s.RunAsync(["upgrade", $"pkg{i}=1.0"], TimeSpan.FromSeconds(30), lines.Add, CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains($"got upgrade pkg{i}=1.0", r.StdOut);
        }
        Assert.Equal(1, Starts);
        Assert.Equal(["got upgrade pkg1=1.0", "got upgrade pkg2=1.0", "got upgrade pkg3=1.0"], lines);
        Assert.Equal(7, (await s.RunAsync(["fail"], TimeSpan.FromSeconds(30), null, CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task Dismissed_authorization_is_reported_like_a_refused_pkexec_and_asked_again_next_time()
    {
        using var s = new AptRootSession(["/bin/bash", Fake("echo 'Error: Not authorized' >&2\nexit 126\n")]);
        var r = await s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, CancellationToken.None);
        Assert.Equal(126, r.ExitCode);
        Assert.Equal("", r.StdOut);
        Assert.Contains("Not authorized", r.StdErr);
        await s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, CancellationToken.None);
        Assert.Equal(2, Starts);
    }

    [Fact]
    public async Task A_helper_that_stops_mid_request_is_a_failure_never_a_success()
    {
        using var s = new AptRootSession(["/bin/bash", Fake("echo R\nread -r line\nexit 0\n")]);
        var r = await s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, CancellationToken.None);
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public async Task A_helper_that_has_exited_is_replaced_by_the_next_request()
    {
        using var s = new AptRootSession(["/bin/bash", Fake("echo R\nread -r id verb\necho \"X $id 0\"\n")]);
        Assert.Equal(0, (await s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, CancellationToken.None)).ExitCode);
        await Task.Delay(300, CancellationToken.None);
        Assert.Equal(0, (await s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, CancellationToken.None)).ExitCode);
        Assert.Equal(2, Starts);
    }

    [Fact]
    public async Task A_slow_request_times_out_and_cancellation_is_honoured()
    {
        using var s = new AptRootSession(["/bin/bash", Fake("echo R\nsleep 30\n")]);
        Assert.True((await s.RunAsync(["update"], TimeSpan.FromMilliseconds(300), null, CancellationToken.None)).TimedOut);
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(["update"], TimeSpan.FromSeconds(30), null, cts.Token));
    }

    [Theory]
    [InlineData("vim\n99 purge vim")]
    [InlineData("vim extra")]
    [InlineData("")]
    public async Task A_request_can_never_carry_a_second_request(string arg)
    {
        using var s = new AptRootSession(["/bin/bash", Fake(Echo)]);
        await Assert.ThrowsAsync<ArgumentException>(() => s.RunAsync(["install", arg], TimeSpan.FromSeconds(30), null, CancellationToken.None));
        Assert.Equal(0, Starts);
    }

    [Fact]
    public async Task Provider_installs_share_one_session_and_never_start_pkexec_themselves()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, "", "", false));
        using var session = new AptRootSession(["/bin/bash", Fake(Echo)]);
        using var apt = new AptProvider(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<AptProvider>.Instance, "/fake/bin", () => false, () => false, "/fake/helper") { Session = session };
        foreach (var name in (string[])["htop", "vim", "curl"])
        {
            var r = await apt.InstallAsync(new UpdateForge.Core.Models.InstallRequest(name, null), new Progress<UpdateForge.Core.Models.UpdateProgress>(), CancellationToken.None);
            Assert.Equal(UpdateForge.Core.Models.UpdateOutcome.Succeeded, r.Outcome);
        }
        Assert.Equal(1, Starts);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Real_helper_frames_output_refuses_bad_requests_and_keeps_serving()
    {
        var helper = FindHelper();
        // A user namespace makes the helper believe it is root; apt-get itself then fails on the locks, which is all this needs.
        if (helper is null || !CanUnshare()) return;
        using var s = new AptRootSession(["/usr/bin/unshare", "-Ur", helper, "serve"]);
        var bad = await s.RunAsync(["install", "--version"], TimeSpan.FromSeconds(60), null, CancellationToken.None);
        Assert.Equal(2, bad.ExitCode);
        Assert.Contains("invalid command", bad.StdOut);
        var real = await s.RunAsync(["install", "uf-no-such-package"], TimeSpan.FromSeconds(60), null, CancellationToken.None);
        Assert.Equal(100, real.ExitCode);
        var again = await s.RunAsync(["upgrade", "vim"], TimeSpan.FromSeconds(60), null, CancellationToken.None);   // no version: refused
        Assert.Equal(2, again.ExitCode);
    }

    private static string? FindHelper()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "packaging", "linux", "updateforge-apt-helper");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static bool CanUnshare()
    {
        if (!File.Exists("/usr/bin/unshare")) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/unshare", ["-Ur", "true"]) { UseShellExecute = false });
            return p!.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
