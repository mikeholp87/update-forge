using UpdateForge.Core.Updates;

namespace UpdateForge.Application;

public enum ScanInterval { OnLaunch, OnLogin, Every6Hours, Every12Hours, Daily, Weekly, Custom }

/// <summary>Versioned, validated settings. Defaults are conservative: daily checks, manual installation.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public AutomaticMode Mode { get; init; } = AutomaticMode.Manual;
    public ScanInterval Interval { get; init; } = ScanInterval.Daily;
    public int CustomIntervalHours { get; init; } = 24;
    public bool CheckAtStartup { get; init; } = true;
    public IReadOnlyList<string> EnabledProviders { get; init; } = ["winget", "registry", "msix", "scoop", "chocolatey"];
    public IReadOnlyList<string> BlockedSources { get; init; } = [];
    public IReadOnlyList<string> PortableDirectories { get; init; } = [];
    public bool RunOnBattery { get; init; }
    public bool RunOnMeteredConnection { get; init; }
    public bool RetryFailedUpdates { get; init; } = true;
    /// <summary>Tray "Pause automatic updates": scheduled runs scan but never install while true.</summary>
    public bool AutomaticPaused { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool CloseToTray { get; init; }
    public bool LaunchAtLogin { get; init; }
    public string Theme { get; init; } = "System";
    public bool NotifyNewUpdates { get; init; } = true;
    public bool NotifyCompleted { get; init; } = true;
    public bool NotifyFailed { get; init; } = true;
    public bool NotifyRestart { get; init; } = true;

    /// <summary>Returns a copy with out-of-range or unknown values replaced by safe defaults.</summary>
    public AppSettings Validated()
    {
        var mode = Enum.IsDefined(Mode) ? Mode : AutomaticMode.Manual;
        // Managed automatic mode stays disabled until the elevated execution mechanism is independently tested.
        if (mode == AutomaticMode.ManagedAutomatic) mode = AutomaticMode.AutomaticSafe;
        return this with
        {
            Version = CurrentVersion,
            Mode = mode,
            Interval = Enum.IsDefined(Interval) ? Interval : ScanInterval.Daily,
            CustomIntervalHours = Math.Clamp(CustomIntervalHours, 1, 24 * 30),
            Theme = Theme is "Light" or "Dark" ? Theme : "System",
            EnabledProviders = [.. EnabledProviders.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }
}
