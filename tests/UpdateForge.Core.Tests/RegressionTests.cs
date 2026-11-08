using UpdateForge.Core.Inventory;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;
using Xunit;

namespace UpdateForge.Core.Tests;

public class MergerRegressionTests
{
    [Fact]
    public void Folding_a_registry_row_never_downgrades_the_packages_own_identity()
    {
        var pkg = new InstalledApplication
        {
            Id = "winget:N.N", Name = "Notepad++", ProviderId = "winget", PackageId = "N.N", PackageSource = "winget", InstalledVersion = "8.9",
            AvailableVersion = "9.0", Status = UpdateStatus.UpdateAvailable, Support = UpdateSupport.AutoUpdatable,
        };
        var reg = new InstalledApplication { Id = "registry:k", Name = "Notepad++", ProviderId = "registry", InstalledVersion = "8.9", Status = UpdateStatus.Unknown, Support = UpdateSupport.Unsupported, Evidence = [new SourceEvidence("registry", "k")] };
        var merged = Assert.Single(InventoryMerger.Merge([pkg, reg]));
        Assert.Equal(IdentityConfidence.Exact, merged.Confidence);
        Assert.True(UpdatePolicy.Evaluate(merged, new UpdatePolicySettings { Mode = AutomaticMode.AutomaticSafe }, unattended: true).IsAllowed);
        Assert.Contains(merged.Evidence, e => e.Detail!.StartsWith("registry link", StringComparison.Ordinal));
    }

    [Fact]
    public void Msix_inventory_rows_fold_into_matching_msstore_package()
    {
        var pkg = new InstalledApplication { Id = "winget:XP1", Name = "Sample App", ProviderId = "winget", PackageId = "XP1", PackageSource = "msstore", InstalledVersion = "3.0.23", Status = UpdateStatus.UpToDate, Support = UpdateSupport.ApprovalRequired };
        var msix = new InstalledApplication { Id = "msix:Sample_1", Name = "Sample App", ProviderId = "msix", InstalledVersion = "3.0.23.0", Status = UpdateStatus.Unknown, Support = UpdateSupport.ManualRequired };
        Assert.Single(InventoryMerger.Merge([pkg, msix]));
    }
}
