using UpdateForge.Application;
using UpdateForge.Background;
using UpdateForge.Core.Processes;
using Xunit;

namespace UpdateForge.Core.Tests;

public class SystemdScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uf-systemd-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe = Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(_exe);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);

    [Fact]
    public void Daily_default_writes_a_persistent_calendar_timer_and_enables_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "systemd unit paths are Linux paths; a Windows temp path contains backslashes.");
        var runner = new ScriptedRunner(_ => Ok());
        new SystemdScheduleService(runner, _dir).Apply(new AppSettings(), _exe);

        var timer = File.ReadAllText(Path.Combine(_dir, "updateforge.timer"));
        Assert.Contains("OnCalendar=*-*-* 10:00:00", timer);
        Assert.Contains("Persistent=true", timer);
        var service = File.ReadAllText(Path.Combine(_dir, "updateforge.service"));
        Assert.Contains($"ExecStart=\"{_exe}\" background run", service);
        Assert.Contains("ConditionACPower=true", service);          // battery is off by default
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(["--user", "enable", "--now", "updateforge.timer"]));
        Assert.All(runner.Calls, c => Assert.Equal("--user", c.Arguments[0]));   // never the system manager
    }

    [Theory]
    [InlineData(ScanInterval.OnLogin, "OnStartupSec=2min")]
    [InlineData(ScanInterval.Every6Hours, "OnCalendar=*-*-* 00,06,12,18:00:00")]
    [InlineData(ScanInterval.Every12Hours, "OnCalendar=*-*-* 00,12:00:00")]
    [InlineData(ScanInterval.Weekly, "OnCalendar=Mon *-*-* 10:00:00")]
    public void Intervals_map_to_systemd_triggers(ScanInterval interval, string expected) =>
        Assert.Contains(expected, SystemdScheduleService.TimerUnit(new AppSettings { Interval = interval }));

    [Fact]
    public void Custom_interval_is_in_hours_and_clamped()
    {
        Assert.Contains("OnUnitActiveSec=72h", SystemdScheduleService.TimerUnit(new AppSettings { Interval = ScanInterval.Custom, CustomIntervalHours = 72 }));
        Assert.Contains("OnUnitActiveSec=1h", SystemdScheduleService.TimerUnit(new AppSettings { Interval = ScanInterval.Custom, CustomIntervalHours = -5 }));
    }

    [Fact]
    public void Running_on_battery_drops_the_ac_condition() =>
        Assert.DoesNotContain("ConditionACPower", SystemdScheduleService.ServiceUnit(new AppSettings { RunOnBattery = true }, "/opt/uf/updateforge-cli"));

    [Fact]
    public void On_launch_removes_instead_of_scheduling()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "systemd unit paths are Linux paths; a Windows temp path contains backslashes.");
        var runner = new ScriptedRunner(_ => Ok());
        var svc = new SystemdScheduleService(runner, _dir);
        svc.Apply(new AppSettings(), _exe);
        svc.Apply(new AppSettings { Interval = ScanInterval.OnLaunch }, _exe);
        Assert.False(File.Exists(Path.Combine(_dir, "updateforge.timer")));
        Assert.False(File.Exists(Path.Combine(_dir, "updateforge.service")));
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("disable"));
    }

    [Theory]
    [InlineData("/opt/up\"date/x")]
    [InlineData("/opt/100%/x")]
    [InlineData("/opt/a$b/x")]
    [InlineData("/opt/a\nExecStartPost=/bin/evil")]
    public void Executable_paths_that_could_inject_directives_are_refused(string path)
    {
        var runner = new ScriptedRunner(_ => Ok());
        // Create the file so the failure is the character check, not a missing file.
        Directory.CreateDirectory(_dir);
        Assert.ThrowsAny<Exception>(() => new SystemdScheduleService(runner, _dir).Apply(new AppSettings(), path));
        Assert.False(File.Exists(Path.Combine(_dir, "updateforge.service")));
    }

    [Fact]
    public void Failed_enable_is_reported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "systemd unit paths are Linux paths; a Windows temp path contains backslashes.");
        var runner = new ScriptedRunner(s => s.Arguments.Contains("enable") ? new ProcessResult(1, "", "Failed to connect to bus", false) : Ok());
        var ex = Assert.Throws<InvalidOperationException>(() => new SystemdScheduleService(runner, _dir).Apply(new AppSettings(), _exe));
        Assert.Contains("Failed to connect to bus", ex.Message);
    }

    [Fact]
    public void Status_reads_next_and_last_run_from_systemd_json()
    {
        var json = """[{"next":1796573820000000,"last":1794224562139548,"unit":"updateforge.timer","activates":"updateforge.service"}]""";
        var runner = new ScriptedRunner(s => s.Arguments.Contains("is-enabled") ? Ok("enabled\n") : s.Arguments.Contains("list-timers") ? Ok(json) : Ok());
        var svc = new SystemdScheduleService(runner, _dir);
        Assert.False(svc.GetStatus().Exists);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "updateforge.timer"), "[Timer]\n");
        var st = svc.GetStatus();
        Assert.True(st.Exists);
        Assert.True(st.Enabled);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1796573820).LocalDateTime, st.NextRun);
        Assert.NotNull(st.LastRun);
    }
}

public class NetworkProbeTests
{
    [Theory]
    [InlineData("yes\n", true)]
    [InlineData("yes (guessed)\n", true)]
    [InlineData("no (guessed)\n", false)]
    [InlineData("unknown\n", false)]
    public async Task Nmcli_output_decides_metered(string output, bool expected)
    {
        var fake = Path.GetTempFileName();
        try
        {
            var probe = new NmcliNetworkCostProbe(new ScriptedRunner(_ => new ProcessResult(0, output, "", false)), fake);
            Assert.Equal(expected, await probe.IsMeteredAsync(CancellationToken.None));
        }
        finally { File.Delete(fake); }
    }

    [Fact]
    public async Task Missing_nmcli_never_blocks() =>
        Assert.False(await new NmcliNetworkCostProbe(new ScriptedRunner(_ => new ProcessResult(0, "yes", "", false)), "/nonexistent/nmcli").IsMeteredAsync(CancellationToken.None));
}
