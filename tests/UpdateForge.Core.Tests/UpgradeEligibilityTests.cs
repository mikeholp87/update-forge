using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;
using Xunit;

namespace UpdateForge.Core.Tests;

public class UpgradeEligibilityTests
{
    private static InstalledApplication App(string id, string installed, bool update = true) => new()
    {
        Id = id, Name = id, ProviderId = "winget", PackageId = id, InstalledVersion = installed,
        AvailableVersion = update ? "2.0" : null, Status = update ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
        Support = UpdateSupport.AutoUpdatable,
    };

    private static HistoryRecord Failure(string id, string previous, int? exit) =>
        new(1, DateTimeOffset.UtcNow, id, id, "winget", previous, "2.0", null, JobState.ManualActionRequired, TimeSpan.Zero, exit,
            ErrorCategory.UserInteractionRequired, false, "x", null);

    [Fact]
    public void App_that_winget_refused_for_install_type_is_removed_from_updates()
    {
        var apps = new[] { App("winget:Tm", "0.1.0"), App("winget:Other", "1.0") };
        var result = UpgradeEligibility.Apply(apps, [Failure("winget:Tm", "0.1.0", UpgradeEligibility.WinGetDifferentInstallTechnology)]);

        var tm = result.Single(a => a.Id == "winget:Tm");
        Assert.False(tm.UpdateAvailable);
        Assert.False(tm.CanAutoUpdate);
        Assert.Null(tm.AvailableVersion);
        Assert.Equal(UpdateSupport.Unsupported, tm.Support);
        Assert.Contains("different installer", tm.Note, StringComparison.Ordinal);
        Assert.True(result.Single(a => a.Id == "winget:Other").UpdateAvailable); // unrelated apps untouched
    }

    [Fact]
    public void Reinstalling_through_winget_lifts_the_restriction()
    {
        // The installed version changed (e.g. reinstalled), so the old failure no longer applies.
        var result = UpgradeEligibility.Apply([App("winget:Tm", "1.4.0")], [Failure("winget:Tm", "0.1.0", UpgradeEligibility.WinGetDifferentInstallTechnology)]);
        Assert.True(result.Single().UpdateAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1603)]
    public void Other_failures_do_not_hide_an_update(int? exit)
    {
        var result = UpgradeEligibility.Apply([App("winget:Tm", "0.1.0")], [Failure("winget:Tm", "0.1.0", exit)]);
        Assert.True(result.Single().UpdateAvailable);
    }

    [Fact]
    public void Apps_without_an_update_are_left_alone()
    {
        var result = UpgradeEligibility.Apply([App("winget:Tm", "0.1.0", update: false)], [Failure("winget:Tm", "0.1.0", UpgradeEligibility.WinGetDifferentInstallTechnology)]);
        Assert.Equal(UpdateStatus.UpToDate, result.Single().Status);
        Assert.Null(result.Single().Note);
    }
}
