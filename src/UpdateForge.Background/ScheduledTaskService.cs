using Microsoft.Win32.TaskScheduler;
using UpdateForge.Application;

namespace UpdateForge.Background;

/// <summary>
/// Windows Task Scheduler integration. The task runs as the current interactive user with limited privileges
/// ("Run only when user is logged on"); no password is stored and no SYSTEM or elevated task is created.
/// </summary>
public sealed class ScheduledTaskService : IScheduleService
{
    public const string FolderName = "UpdateForge";
    public const string TaskName = "Scheduled Scan";

    /// <summary>Command-line executable the task runs. It must live next to the GUI executable (same install folder).</summary>
    public const string CliExecutableName = "updateforge-cli.exe";

    public static string DefaultExecutablePath => Path.Combine(AppContext.BaseDirectory, CliExecutableName);
    private static string FullName => $@"\{FolderName}\{TaskName}";

    public ScheduleStatus GetStatus()
    {
        using var ts = new TaskService();
        var t = ts.GetTask(FullName);
        return t is null ? new ScheduleStatus(false, false, null, null, null)
            : new ScheduleStatus(true, t.Enabled, t.Enabled ? t.NextRunTime : null, t.LastRunTime == DateTime.MinValue ? null : t.LastRunTime, t.State.ToString());
    }

    public void Apply(AppSettings settings, string executablePath)
    {
        if (settings.Interval == ScanInterval.OnLaunch) { Remove(); return; }
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("The UpdateForge command-line executable was not found; cannot register the scheduled task.", executablePath);

        using var ts = new TaskService();
        var def = ts.NewTask();
        def.RegistrationInfo.Description = "Checks for software updates with UpdateForge and installs approved updates.";
        def.RegistrationInfo.Author = "UpdateForge";
        def.Principal.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        def.Principal.LogonType = TaskLogonType.InteractiveToken;
        def.Principal.RunLevel = TaskRunLevel.LUA; // never elevated

        var s = def.Settings;
        s.StartWhenAvailable = true;                       // run missed schedules when possible
        s.DisallowStartIfOnBatteries = !settings.RunOnBattery;
        s.StopIfGoingOnBatteries = !settings.RunOnBattery;
        s.RunOnlyIfNetworkAvailable = true;
        s.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        s.ExecutionTimeLimit = TimeSpan.FromHours(2);
        s.AllowHardTerminate = true;

        var start = DateTime.Today.AddHours(10);
        if (start < DateTime.Now) start = start.AddDays(1);
        switch (settings.Interval)
        {
            case ScanInterval.OnLogin:
                def.Triggers.Add(new LogonTrigger { UserId = def.Principal.UserId });
                break;
            case ScanInterval.Every6Hours:
                def.Triggers.Add(Repeating(TimeSpan.FromHours(6)));
                break;
            case ScanInterval.Every12Hours:
                def.Triggers.Add(Repeating(TimeSpan.FromHours(12)));
                break;
            case ScanInterval.Weekly:
                def.Triggers.Add(new WeeklyTrigger { StartBoundary = start, DaysOfWeek = DaysOfTheWeek.Monday });
                break;
            case ScanInterval.Custom:
                def.Triggers.Add(Repeating(TimeSpan.FromHours(settings.CustomIntervalHours)));
                break;
            default:
                def.Triggers.Add(new DailyTrigger { StartBoundary = start, DaysInterval = 1 });
                break;
        }

        // Fixed executable and constant arguments only; nothing user- or network-supplied reaches the command line.
        def.Actions.Add(new ExecAction(executablePath, "background run", Path.GetDirectoryName(executablePath)));

        var folder = ts.RootFolder.SubFolders.Exists(FolderName) ? ts.RootFolder.SubFolders[FolderName] : ts.RootFolder.CreateFolder(FolderName);
        folder.RegisterTaskDefinition(TaskName, def, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);
    }

    private static DailyTrigger Repeating(TimeSpan every)
    {
        var t = new DailyTrigger { StartBoundary = DateTime.Now.AddMinutes(5), DaysInterval = 1 };
        t.Repetition.Interval = every;
        t.Repetition.Duration = TimeSpan.FromDays(1);
        return t;
    }

    public void Remove()
    {
        using var ts = new TaskService();
        if (ts.GetTask(FullName) is not null && ts.RootFolder.SubFolders.Exists(FolderName))
            ts.RootFolder.SubFolders[FolderName].DeleteTask(TaskName, false);
    }
}
