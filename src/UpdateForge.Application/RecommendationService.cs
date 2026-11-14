using UpdateForge.Core.Models;
using UpdateForge.Core.Recommendations;

namespace UpdateForge.Application;

public sealed record RecommendationReport(IReadOnlyList<Recommendation> Items, IReadOnlyList<Interest> Interests, int DismissedCount, bool Learning);

/// <summary>
/// Suggests packages that fit what is already installed, and learns what the user is into. The suggestions come from a reviewed catalog in the
/// core library (nothing is fetched), are only offered through a manager that is enabled and usable on this machine, and can be dismissed for good.
/// Learning is local counting (see <see cref="InterestModel"/>): it can be switched off and wiped at any time.
/// </summary>
public sealed class RecommendationService(PackageManagerService managers, ISettingsStore settingsStore, IInterestStore interestStore, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<RecommendationReport> GetAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var profile = settings.LearnInterests ? await LearnCoreAsync(installed, ct).ConfigureAwait(false) : new InterestProfile();
        var statuses = await managers.GetStatusAsync(ct).ConfigureAwait(false);

        var installable = statuses.Where(s => s.Enabled && s.Availability.IsAvailable && s.Capabilities.CanUpdate)
            .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A real package manager that is on the machine but switched off: its apps are silently missing from every list.
        var disabled = statuses.Where(s => !s.Enabled && s.SupportsSources && s.Availability.IsAvailable).Select(s => s.DisplayName).ToList();
        var dismissed = settings.DismissedRecommendations.ToHashSet(StringComparer.Ordinal);

        var items = RecommendationEngine.Generate(new RecommendationContext
        {
            Installed = installed,
            InstallableProviders = installable,
            DisabledManagers = disabled,
            Dismissed = dismissed,
            Profile = profile,
        });
        var interests = InterestModel.Compute(profile, installed).Where(i => i.Score >= InterestModel.Threshold).ToList();
        return new RecommendationReport(items, interests, dismissed.Count, settings.LearnInterests);
    }

    /// <summary>Notices programs that are new since the last look. Called after every scan so learning does not depend on opening the page.</summary>
    public async Task LearnAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (settings.LearnInterests && installed.Count > 0) await LearnCoreAsync(installed, ct).ConfigureAwait(false);
    }

    private async Task<InterestProfile> LearnCoreAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken ct)
    {
        var profile = await interestStore.LoadAsync(ct).ConfigureAwait(false);
        if (installed.Count == 0) return profile;
        var updated = InterestModel.Learn(profile, installed, _clock.GetUtcNow());
        await interestStore.SaveAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Hides a suggestion for good, and counts it as a no-thanks for its niche.</summary>
    public async Task DismissAsync(Recommendation item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (!settings.DismissedRecommendations.Contains(item.Key, StringComparer.Ordinal))
            await settingsStore.SaveAsync(settings with { DismissedRecommendations = [.. settings.DismissedRecommendations, item.Key] }, ct).ConfigureAwait(false);
        if (settings.LearnInterests && item.Niches is { Count: > 0 })
            await interestStore.SaveAsync(InterestModel.RecordDismissed(await interestStore.LoadAsync(ct).ConfigureAwait(false), item.Niches, _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }

    /// <summary>A suggestion was installed: that niche is a keeper.</summary>
    public async Task RecordInstalledAsync(Recommendation item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (settings.LearnInterests && item.Niches is { Count: > 0 })
            await interestStore.SaveAsync(InterestModel.RecordInstalled(await interestStore.LoadAsync(ct).ConfigureAwait(false), item.Niches, _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }

    public async Task RestoreDismissedAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        await settingsStore.SaveAsync(settings with { DismissedRecommendations = [] }, ct).ConfigureAwait(false);
    }

    public async Task SetLearningAsync(bool enabled, CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        await settingsStore.SaveAsync(settings with { LearnInterests = enabled }, ct).ConfigureAwait(false);
    }

    /// <summary>Wipes everything learned. The next look starts from a fresh baseline, so what is installed today is not mistaken for new.</summary>
    public Task ForgetAsync(CancellationToken ct) => interestStore.SaveAsync(new InterestProfile(), ct);
}
