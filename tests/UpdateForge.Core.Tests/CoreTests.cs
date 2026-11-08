using UpdateForge.Core.Inventory;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;
using UpdateForge.Core.Versioning;
using Xunit;

namespace UpdateForge.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.0", "1.0.0", VersionOrder.Equal)]
    [InlineData("1.9", "1.10", VersionOrder.Older)]
    [InlineData("24.09", "25.01", VersionOrder.Older)]
    [InlineData("2.0", "1.99.9", VersionOrder.Newer)]
    [InlineData("v3.0.21", "3.0.22", VersionOrder.Older)]
    [InlineData("1.0.0-beta", "1.0.0", VersionOrder.Older)]
    [InlineData("1.0.0", "1.0.0-rc1", VersionOrder.Newer)]
    [InlineData("1.0.0+build5", "1.0.0", VersionOrder.Equal)]
    public void Compares_versions(string a, string b, VersionOrder expected) => Assert.Equal(expected, AppVersion.Compare(a, b));

    [Theory]
    [InlineData("Unknown", "1.0")]
    [InlineData("1.0", "Unknown")]
    [InlineData("", "1.0")]
    [InlineData(null, "1.0")]
    [InlineData("< 1.2", "1.3")]
    [InlineData("abc", "def")]
    public void Unparseable_versions_are_never_reported_as_newer(string? a, string? b)
    {
        Assert.Equal(VersionOrder.Unknown, AppVersion.Compare(a, b));
        Assert.False(AppVersion.IsNewer(a, b));
    }

    [Fact]
    public void Huge_numeric_segments_do_not_throw() => Assert.Equal(VersionOrder.Unknown, AppVersion.Compare("99999999999999999999999.1", "1.0"));
}

public class MergerTests
{
    private static InstalledApplication Reg(string name, string? ver, string? pub = null, string key = "k", InstallScope scope = InstallScope.Machine, string? code = null) => new()
    {
        Id = $"registry:{key}", Name = name, ProviderId = "registry", InstalledVersion = ver, Publisher = pub, Scope = scope, ProductCode = code,
        Status = UpdateStatus.Unknown, Support = UpdateSupport.Unsupported, Evidence = [new SourceEvidence("registry", key)],
    };

    private static InstalledApplication Pkg(string id, string name, string? ver, string? pub = null, InstallScope scope = InstallScope.Unknown) => new()
    {
        Id = $"winget:{id}", Name = name, ProviderId = "winget", PackageId = id, InstalledVersion = ver, Publisher = pub, Scope = scope,
        Status = UpdateStatus.UpToDate, Support = UpdateSupport.AutoUpdatable, Evidence = [new SourceEvidence("winget", id)],
    };

    [Fact]
    public void Registry_entry_folds_into_matching_package()
    {
        var result = InventoryMerger.Merge([Pkg("VideoLAN.VLC", "VLC media player", "3.0.21"), Reg("VLC media player", "3.0.21", "VideoLAN", "vlc")]);
        var single = Assert.Single(result);
        Assert.Equal("winget", single.ProviderId);
        Assert.Equal("VideoLAN", single.Publisher);
        Assert.Equal(2, single.Evidence.Count);
    }

    [Fact]
    public void Architecture_suffix_and_version_in_name_are_ignored_when_matching()
    {
        var result = InventoryMerger.Merge([Pkg("Notepad++.Notepad++", "Notepad++ (64-bit x64)", "8.9.8.1"), Reg("Notepad++ (64-bit x64)", "8.9.8.1")]);
        Assert.Single(result);
        var r2 = InventoryMerger.Merge([Pkg("TDF.LibreOffice", "LibreOffice 26.8", "26.8"), Reg("LibreOffice", "26.8")]);
        Assert.Single(r2);
    }

    [Fact]
    public void Different_versions_are_not_merged()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "2.0"), Reg("Thing", "1.0")]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Conflicting_publishers_are_not_merged()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "1.0", "Alpha Inc"), Reg("Thing", "1.0", "Beta Corp")]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Ambiguous_registry_rows_are_not_folded_into_either()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "1.0"), Reg("Thing", "1.0", key: "one"), Reg("Thing", "1.0", key: "two")]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void One_registry_row_matching_two_packages_stays_separate_with_low_confidence()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "1.0"), Pkg("C.D", "Thing", "1.0"), Reg("Thing", "1.0")]);
        Assert.Equal(3, result.Count);
        Assert.Equal(IdentityConfidence.Low, result.Single(r => r.ProviderId == "registry").Confidence);
    }

    [Fact]
    public void Same_name_in_different_scope_is_not_merged()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "1.0", scope: InstallScope.User), Reg("Thing", "1.0", scope: InstallScope.Machine)]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Duplicate_package_ids_in_same_scope_collapse()
    {
        var result = InventoryMerger.Merge([Pkg("A.B", "Thing", "1.0"), Pkg("a.b", "Thing", "1.0")]);
        Assert.Single(result);
    }

    [Fact]
    public void Product_code_match_is_exact_confidence()
    {
        var pkg = Pkg("A.B", "Thing", "1.0") with { ProductCode = "{11111111-1111-1111-1111-111111111111}" };
        var result = InventoryMerger.Merge([pkg, Reg("Totally Different Name", "1.0", code: "{11111111-1111-1111-1111-111111111111}")]);
        var single = Assert.Single(result);
        Assert.Equal(IdentityConfidence.Exact, single.Confidence);
    }
}

public class PolicyTests
{
    private static InstalledApplication App(Action<InstalledApplicationBuilder>? tweak = null)
    {
        var b = new InstalledApplicationBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    internal sealed class InstalledApplicationBuilder
    {
        public UpdateStatus Status = UpdateStatus.UpdateAvailable;
        public UpdateSupport Support = UpdateSupport.AutoUpdatable;
        public bool Pinned, Excluded, Elevation, Interaction;
        public IdentityConfidence Confidence = IdentityConfidence.Exact;
        public string? Source = "winget";
        public InstalledApplication Build() => new()
        {
            Id = "winget:A.B", Name = "A", ProviderId = "winget", PackageId = "A.B", PackageSource = Source, InstalledVersion = "1.0", AvailableVersion = "2.0",
            Status = Status, Support = Support, IsPinned = Pinned, IsExcluded = Excluded, RequiresElevation = Elevation,
            RequiresUserInteraction = Interaction, Confidence = Confidence,
        };
    }

    private static UpdatePolicySettings Settings(AutomaticMode mode = AutomaticMode.AutomaticSafe) => new() { Mode = mode };

    [Fact] public void Plain_manual_update_is_allowed() => Assert.True(UpdatePolicy.Evaluate(App(), Settings(AutomaticMode.Manual), false).IsAllowed);
    [Fact] public void Unknown_status_is_not_updated() => Assert.Equal(PolicyVerdict.Skip, UpdatePolicy.Evaluate(App(b => b.Status = UpdateStatus.Unknown), Settings(), false).Verdict);
    [Fact] public void Excluded_is_skipped() => Assert.Equal(PolicyVerdict.Skip, UpdatePolicy.Evaluate(App(b => b.Excluded = true), Settings(), false).Verdict);
    [Fact] public void Pinned_is_blocked() => Assert.Equal(PolicyVerdict.Block, UpdatePolicy.Evaluate(App(b => b.Pinned = true), Settings(), false).Verdict);
    [Fact] public void Unsupported_is_reported() => Assert.Equal(PolicyVerdict.Unsupported, UpdatePolicy.Evaluate(App(b => b.Support = UpdateSupport.Unsupported), Settings(), false).Verdict);
    [Fact] public void Low_confidence_never_auto_replaces() => Assert.Equal(PolicyVerdict.NeedsApproval, UpdatePolicy.Evaluate(App(b => b.Confidence = IdentityConfidence.Low), Settings(), false).Verdict);
    [Fact] public void Blocked_source_is_blocked() => Assert.Equal(PolicyVerdict.Block, UpdatePolicy.Evaluate(App(), Settings() with { BlockedSources = new HashSet<string> { "winget" } }, false).Verdict);
    [Fact] public void Unattended_manual_mode_installs_nothing() => Assert.Equal(PolicyVerdict.Skip, UpdatePolicy.Evaluate(App(), Settings(AutomaticMode.Manual), true).Verdict);
    [Fact] public void Unattended_elevation_is_deferred() => Assert.Equal(PolicyVerdict.NeedsApproval, UpdatePolicy.Evaluate(App(b => b.Elevation = true), Settings(), true).Verdict);
    [Fact] public void Unattended_interaction_is_deferred() => Assert.Equal(PolicyVerdict.NeedsApproval, UpdatePolicy.Evaluate(App(b => b.Interaction = true), Settings(), true).Verdict);
    [Fact] public void Unattended_safe_update_is_allowed() => Assert.True(UpdatePolicy.Evaluate(App(), Settings(), true).IsAllowed);

    [Fact]
    public void Skipped_version_is_skipped_but_newer_version_is_not()
    {
        var s = Settings() with { SkippedVersions = [new SkippedVersion("winget:A.B", "2.0")] };
        Assert.Equal(PolicyVerdict.Skip, UpdatePolicy.Evaluate(App(), s, false).Verdict);
        var newer = App() with { AvailableVersion = "2.1" };
        Assert.True(UpdatePolicy.Evaluate(newer, s, false).IsAllowed);
    }

    [Fact]
    public void Disabled_provider_is_blocked() =>
        Assert.Equal(PolicyVerdict.Block, UpdatePolicy.Evaluate(App(), Settings() with { EnabledProviders = new HashSet<string> { "scoop" } }, false).Verdict);
}

public class StateMachineTests
{
    [Fact] public void Happy_path_is_legal()
    {
        var s = JobState.Queued;
        foreach (var next in new[] { JobState.Downloading, JobState.Verifying, JobState.Installing, JobState.VerifyingInstallation, JobState.Completed })
            s = JobStateMachine.Transition(s, next);
        Assert.Equal(JobState.Completed, s);
    }

    [Theory]
    [InlineData(JobState.Completed, JobState.Queued)]
    [InlineData(JobState.Queued, JobState.Completed)]
    [InlineData(JobState.Discovered, JobState.Installing)]
    [InlineData(JobState.Installing, JobState.Completed)] // must pass through verification
    public void Illegal_transitions_throw(JobState from, JobState to) => Assert.Throws<InvalidOperationException>(() => JobStateMachine.Transition(from, to));

    [Fact] public void Failed_can_be_retried() => Assert.True(JobStateMachine.CanTransition(JobState.Failed, JobState.Queued));
    [Fact] public void Installing_is_in_flight() => Assert.True(JobStateMachine.IsInFlight(JobState.Installing));
}

public class SettingsTests
{
    [Fact]
    public void Managed_mode_is_downgraded_until_elevation_is_tested()
    {
        var s = new UpdateForge.Application.AppSettings { Mode = AutomaticMode.ManagedAutomatic }.Validated();
        Assert.Equal(AutomaticMode.AutomaticSafe, s.Mode);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = new UpdateForge.Application.AppSettings { CustomIntervalHours = -5, Theme = "Neon", Mode = (AutomaticMode)99 }.Validated();
        Assert.Equal(1, s.CustomIntervalHours);
        Assert.Equal("System", s.Theme);
        Assert.Equal(AutomaticMode.Manual, s.Mode);
    }

    [Fact] public void Defaults_are_conservative()
    {
        var s = new UpdateForge.Application.AppSettings();
        Assert.Equal(AutomaticMode.Manual, s.Mode);
        Assert.False(s.RunOnBattery);
        Assert.False(s.RunOnMeteredConnection);
    }
}
