using System.Text;
using System.Text.Json;
using UpdateForge.Application;
using UpdateForge.Core.Processes;

namespace UpdateForge.Background;

/// <summary>
/// systemd <b>user</b> timer integration (the Linux counterpart of the Windows scheduled task). The timer and its service live in
/// <c>~/.config/systemd/user</c>, run as the current user, and never use sudo or system-wide units. The service runs the CLI with
/// constant arguments only. User timers fire while the user is logged in (or lingering: <c>loginctl enable-linger</c>).
/// </summary>
public sealed class SystemdScheduleService(IProcessRunner runner, string? unitDirectory = null, string systemctlPath = "/usr/bin/systemctl") : IScheduleService
{
    public const string UnitName = "updateforge";
    public const string CliExecutableName = "updateforge-cli";

    public static string DefaultExecutablePath => Path.Combine(AppContext.BaseDirectory, CliExecutableName);

    private readonly string _dir = unitDirectory ?? Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user");

    private string TimerFile => Path.Combine(_dir, UnitName + ".timer");
    private string ServiceFile => Path.Combine(_dir, UnitName + ".service");

    public ScheduleStatus GetStatus()
    {
        if (!File.Exists(TimerFile)) return new ScheduleStatus(false, false, null, null, null);
        var enabled = Run("is-enabled", UnitName + ".timer").StdOut.Trim() == "enabled";
        DateTime? next = null, last = null;
        var list = Run("list-timers", "--user", "--all", "--output=json", UnitName + ".timer");
        try
        {
            using var doc = JsonDocument.Parse(list.StdOut);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.GetProperty("unit").GetString() != UnitName + ".timer") continue;
                next = FromMicroseconds(t, "next");
                last = FromMicroseconds(t, "last");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { /* older systemd: no JSON output */ }
        return new ScheduleStatus(true, enabled, enabled ? next : null, last, enabled ? "enabled" : "disabled");
    }

    private static DateTime? FromMicroseconds(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var us) && us > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000).LocalDateTime : null;

    public void Apply(AppSettings settings, string executablePath)
    {
        if (settings.Interval == ScanInterval.OnLaunch) { Remove(); return; }
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("The UpdateForge command-line executable was not found; cannot register the timer.", executablePath);
        // The path goes into a unit file: a newline or quote would add directives, and % is a systemd specifier.
        if (executablePath.Any(c => char.IsControl(c) || c is '"' or '%' or '\\' or '$'))
            throw new ArgumentException("The executable path contains characters that cannot be written to a systemd unit.", nameof(executablePath));

        Directory.CreateDirectory(_dir);
        File.WriteAllText(ServiceFile, ServiceUnit(settings, executablePath), new UTF8Encoding(false));
        File.WriteAllText(TimerFile, TimerUnit(settings), new UTF8Encoding(false));
        Check(Run("daemon-reload"), "reload systemd");
        Check(Run("enable", "--now", UnitName + ".timer"), "enable the timer");
        Check(Run("restart", UnitName + ".timer"), "restart the timer");   // pick up a changed schedule
    }

    public void Remove()
    {
        if (!File.Exists(TimerFile) && !File.Exists(ServiceFile)) return;
        Run("disable", "--now", UnitName + ".timer");
        File.Delete(TimerFile);
        File.Delete(ServiceFile);
        Run("daemon-reload");
    }

    internal static string ServiceUnit(AppSettings settings, string executablePath) => string.Join('\n',
    [
        "[Unit]", "Description=UpdateForge scheduled scan",
        .. settings.RunOnBattery ? Array.Empty<string>() : ["ConditionACPower=true"],
        "", "[Service]", "Type=oneshot", $"ExecStart=\"{executablePath}\" background run", "Nice=10", "TimeoutStartSec=2h", "",
    ]);

    internal static string TimerUnit(AppSettings settings)
    {
        var trigger = settings.Interval switch
        {
            ScanInterval.OnLogin => "OnStartupSec=2min",
            ScanInterval.Every6Hours => "OnCalendar=*-*-* 00,06,12,18:00:00\nPersistent=true",
            ScanInterval.Every12Hours => "OnCalendar=*-*-* 00,12:00:00\nPersistent=true",
            ScanInterval.Weekly => "OnCalendar=Mon *-*-* 10:00:00\nPersistent=true",
            ScanInterval.Custom => $"OnStartupSec=5min\nOnUnitActiveSec={Math.Clamp(settings.CustomIntervalHours, 1, 24 * 30)}h",
            _ => "OnCalendar=*-*-* 10:00:00\nPersistent=true",
        };
        return $"""
            [Unit]
            Description=UpdateForge scheduled scan

            [Timer]
            {trigger}
            RandomizedDelaySec=5min

            [Install]
            WantedBy=timers.target

            """;
    }

    private ProcessResult Run(params string[] args) =>
        runner.RunAsync(new ProcessSpec(systemctlPath, ["--user", .. args.Where(a => a != "--user")], TimeSpan.FromSeconds(30)), CancellationToken.None).GetAwaiter().GetResult();

    private static void Check(ProcessResult r, string what)
    {
        if (r.ExitCode != 0) throw new InvalidOperationException($"Could not {what}: {r.StdErr.Trim()}");
    }
}
