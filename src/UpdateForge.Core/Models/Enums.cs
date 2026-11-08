namespace UpdateForge.Core.Models;

/// <summary>Whether a newer version is known to exist. Unknown is never treated as "newer".</summary>
public enum UpdateStatus { Unknown = 0, UpToDate, UpdateAvailable, Unsupported }

/// <summary>How (and whether) UpdateForge may update an application. Tracked separately from discoverability.</summary>
public enum UpdateSupport
{
    /// <summary>Insufficient information to decide.</summary>
    Unknown = 0,
    /// <summary>Detected, but no trustworthy update mechanism exists.</summary>
    Unsupported,
    /// <summary>A vendor-managed path exists but UpdateForge has no approved automation.</summary>
    ManualRequired,
    /// <summary>An update exists but needs user interaction or extra authorization.</summary>
    ApprovalRequired,
    /// <summary>A supported provider can perform the update unattended.</summary>
    AutoUpdatable,
}

public enum InstallScope { Unknown = 0, User, Machine }

public enum IdentityConfidence { Low = 0, Medium, High, Exact }

public enum ProviderHealth { Unknown = 0, Available, Unavailable, Degraded }

public enum UpdateOutcome { Succeeded, RestartRequired, Failed, ManualActionRequired, Blocked, Canceled, NoChange }

public enum ErrorCategory
{
    None = 0, Network, ProviderUnavailable, AgreementsNotAccepted, NoApplicableInstaller, HashMismatch,
    ElevationRequired, ApplicationInUse, InstallerFailed, InstallerRunning, DiskSpace, PendingRestart,
    Timeout, Canceled, VerificationFailed, SecurityBlocked, UserInteractionRequired, Unknown,
}
