using UpdateForge.Core.Models;

namespace UpdateForge.Core.Updates;

public enum AutomaticMode { Manual, AutomaticSafe, ManagedAutomatic }

public enum PolicyVerdict { Allow, Skip, Block, NeedsApproval, Unsupported }

public sealed record PolicyDecision(PolicyVerdict Verdict, string Reason)
{
    public bool IsAllowed => Verdict == PolicyVerdict.Allow;
}

public sealed record SkippedVersion(string ApplicationId, string Version);

public sealed record UpdatePolicySettings
{
    public AutomaticMode Mode { get; init; } = AutomaticMode.Manual;
    public IReadOnlySet<string> ExcludedApplicationIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<SkippedVersion> SkippedVersions { get; init; } = [];
    public IReadOnlySet<string> BlockedSources { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> EnabledProviders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Single place deciding whether an update may run. Shared by GUI, CLI and the background task so the same
/// exclusions, pins, confidence and security rules apply everywhere.
/// </summary>
public static class UpdatePolicy
{
    /// <param name="unattended">True for scheduled/background runs, which are stricter than explicit user clicks.</param>
    public static PolicyDecision Evaluate(InstalledApplication app, UpdatePolicySettings settings, bool unattended)
    {
        if (!app.UpdateAvailable) return new(PolicyVerdict.Skip, "No confirmed newer version.");
        if (settings.ExcludedApplicationIds.Contains(app.Id) || app.IsExcluded) return new(PolicyVerdict.Skip, "Application is excluded.");
        if (app.IsPinned) return new(PolicyVerdict.Block, "Package is pinned by its package manager or by the user.");
        if (settings.EnabledProviders.Count > 0 && !settings.EnabledProviders.Contains(app.ProviderId))
            return new(PolicyVerdict.Block, $"Provider '{app.ProviderId}' is disabled.");
        if (app.PackageSource is { } src && settings.BlockedSources.Contains(src)) return new(PolicyVerdict.Block, $"Source '{src}' is blocked.");
        if (app.AvailableVersion is { } v && settings.SkippedVersions.Any(s =>
                string.Equals(s.ApplicationId, app.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Version, v, StringComparison.OrdinalIgnoreCase)))
            return new(PolicyVerdict.Skip, $"Version {v} was skipped by the user.");
        if (app.Support is UpdateSupport.Unsupported or UpdateSupport.Unknown) return new(PolicyVerdict.Unsupported, "No trustworthy update mechanism.");
        if (app.Support == UpdateSupport.ManualRequired) return new(PolicyVerdict.NeedsApproval, "Manual update required.");
        if (app.Confidence < IdentityConfidence.High && app.Confidence != IdentityConfidence.Exact)
            return new(PolicyVerdict.NeedsApproval, "Identity match is not confident enough for automatic replacement.");

        if (unattended)
        {
            if (settings.Mode == AutomaticMode.Manual) return new(PolicyVerdict.Skip, "Automatic installation is disabled.");
            if (app.RequiresElevation) return new(PolicyVerdict.NeedsApproval, "Requires administrator approval; left pending.");
            if (app.RequiresUserInteraction) return new(PolicyVerdict.NeedsApproval, "Installer needs user interaction.");
            if (app.Support != UpdateSupport.AutoUpdatable) return new(PolicyVerdict.NeedsApproval, "Update requires approval.");
        }
        return new(PolicyVerdict.Allow, "Allowed.");
    }
}
