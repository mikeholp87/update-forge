using UpdateForge.Core.Models;
using UpdateForge.Core.Recommendations;

namespace UpdateForge.Application;

/// <param name="Repositories">The user allows looking through this computer's package metadata.</param>
/// <param name="Online">The user allows the opt-in popularity lookup.</param>
/// <param name="RepositoriesAvailable">This computer has a source for the local lookup (Linux with apt).</param>
/// <param name="OnlineAvailable">This computer has a source for the online lookup (Linux with Flatpak).</param>
public sealed record DiscoveryStatus(bool Repositories, bool Online, bool RepositoriesAvailable, bool OnlineAvailable);

public sealed record RecommendationReport(IReadOnlyList<Recommendation> Items, IReadOnlyList<Interest> Interests, int DismissedCount, bool Learning,
    DiscoveryStatus? Discovery = null);

/// <summary>
/// Suggests packages that fit what is already installed, and learns what the user is into. The suggestions come from a reviewed catalog in the
/// core library (nothing is fetched), are only offered through a manager that is enabled and usable on this machine, and can be dismissed for good.
/// Learning is local counting (see <see cref="InterestModel"/>): it can be switched off and wiped at any time.
/// </summary>
public sealed class RecommendationService(PackageManagerService managers, ISettingsStore settingsStore, IInterestStore interestStore, TimeProvider? clock = null,
    IEnumerable<IDiscoverySource>? sources = null, ISystemSignalSource? signalSource = null)
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20), CacheLife = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IReadOnlyList<IDiscoverySource> _sources = [.. sources ?? []];
    private (DateTimeOffset At, string Key, IReadOnlyList<DiscoveredPackage> Items)? _discoveryCache;
    private (DateTimeOffset At, SystemSignals Value)? _signalCache;

    public async Task<RecommendationReport> GetAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var signals = await ReadSignalsAsync(ct).ConfigureAwait(false);
        var profile = settings.LearnInterests ? await LearnCoreAsync(installed, signals, ct).ConfigureAwait(false) : new InterestProfile();
        var statuses = await managers.GetStatusAsync(ct).ConfigureAwait(false);

        var installable = statuses.Where(s => s.Enabled && s.Availability.IsAvailable && s.Capabilities.CanUpdate)
            .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A real package manager that is on the machine but switched off: its apps are silently missing from every list.
        var disabled = statuses.Where(s => !s.Enabled && s.SupportsSources && s.Availability.IsAvailable).Select(s => s.DisplayName).ToList();
        var dismissed = settings.DismissedRecommendations.ToHashSet(StringComparer.Ordinal);

        var interests = InterestModel.Compute(profile, installed, signals).Where(i => i.Score >= InterestModel.Threshold).ToList();
        var discovered = await DiscoverAsync(settings, installed, interests, ct).ConfigureAwait(false);
        var items = RecommendationEngine.Generate(new RecommendationContext
        {
            Installed = installed,
            InstallableProviders = installable,
            DisabledManagers = disabled,
            Dismissed = dismissed,
            Profile = profile,
            Signals = signals,
            Discovered = discovered,
        });
        var status = new DiscoveryStatus(settings.DiscoverFromRepositories, settings.OnlineDiscovery,
            _sources.Any(s => !s.RequiresNetwork), _sources.Any(s => s.RequiresNetwork));
        return new RecommendationReport(items, interests, dismissed.Count, settings.LearnInterests, status);
    }

    /// <summary>Menu entries and install history, read locally and kept for a few minutes because scans and page visits ask often.</summary>
    private async Task<SystemSignals?> ReadSignalsAsync(CancellationToken ct)
    {
        if (signalSource is null) return null;
        if (_signalCache is { } c && _clock.GetUtcNow() - c.At < TimeSpan.FromMinutes(5)) return c.Value;
        try
        {
            var value = await signalSource.ReadAsync(ct).ConfigureAwait(false);
            _signalCache = (_clock.GetUtcNow(), value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }   // unreadable: recommend from the package list alone
    }

    /// <summary>
    /// Runs the sources the user allows. Each one is bounded in time and a failing one is skipped, so a missing tool or no network never breaks the page.
    /// Online sources only receive interest names, never what is installed. Results are reused for a while so reopening the page costs nothing.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(AppSettings settings, IReadOnlyList<InstalledApplication> installed, IReadOnlyList<Interest> interests, CancellationToken ct)
    {
        var active = _sources.Where(s => s.RequiresNetwork ? settings.OnlineDiscovery : settings.DiscoverFromRepositories).ToList();
        if (active.Count == 0) return [];
        var niches = interests.OrderByDescending(i => i.Score).Take(4).Select(i => i.Niche).ToList();
        var key = $"{settings.DiscoverFromRepositories}|{settings.OnlineDiscovery}|{installed.Count}|{string.Join(',', niches)}";
        if (_discoveryCache is { } c && c.Key == key && _clock.GetUtcNow() - c.At < CacheLife) return c.Items;

        var request = new DiscoveryRequest(installed, niches);
        var found = await Task.WhenAll(active.Select(async source =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);
            try { return await source.DiscoverAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }   // too slow: skip it
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception) { return []; }
        })).ConfigureAwait(false);
        IReadOnlyList<DiscoveredPackage> items = [.. found.SelectMany(f => f)];
        _discoveryCache = (_clock.GetUtcNow(), key, items);
        return items;
    }

    /// <summary>Notices programs that are new since the last look. Called after every scan so learning does not depend on opening the page.</summary>
    public async Task LearnAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        if (settings.LearnInterests && installed.Count > 0) await LearnCoreAsync(installed, await ReadSignalsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private async Task<InterestProfile> LearnCoreAsync(IReadOnlyList<InstalledApplication> installed, SystemSignals? signals, CancellationToken ct)
    {
        var profile = await interestStore.LoadAsync(ct).ConfigureAwait(false);
        if (installed.Count == 0) return profile;
        var updated = InterestModel.Learn(profile, installed, _clock.GetUtcNow(), signals);
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

    /// <summary>Switches the local metadata lookup and/or the opt-in online lookup. Null leaves a choice unchanged.</summary>
    public async Task SetDiscoveryAsync(bool? repositories, bool? online, CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        await settingsStore.SaveAsync(settings with
        {
            DiscoverFromRepositories = repositories ?? settings.DiscoverFromRepositories,
            OnlineDiscovery = online ?? settings.OnlineDiscovery,
        }, ct).ConfigureAwait(false);
        _discoveryCache = null;
    }

    public async Task SetLearningAsync(bool enabled, CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        await settingsStore.SaveAsync(settings with { LearnInterests = enabled }, ct).ConfigureAwait(false);
    }

    /// <summary>Wipes everything learned. The next look starts from a fresh baseline, so what is installed today is not mistaken for new.</summary>
    public Task ForgetAsync(CancellationToken ct) => interestStore.SaveAsync(new InterestProfile { HistorySeeded = true }, ct);
}
