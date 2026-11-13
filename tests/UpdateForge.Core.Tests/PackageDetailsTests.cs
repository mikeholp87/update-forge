using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Npm;
using UpdateForge.Providers.Scoop;
using UpdateForge.Providers.WinGet;
using Xunit;

namespace UpdateForge.Core.Tests;

public class PackageDetailsTests
{
    private const string WinGetShow = """
        Found Git [Git.Git]
        Version: 2.47.1
        Publisher: The Git Development Community
        Publisher Url: https://gitforwindows.org/
        Author: Johannes Schindelin
        Description:
          Git for Windows focuses on offering a lightweight, native set of tools.

          It brings the full feature set of the Git SCM to Windows.
        Homepage: https://gitforwindows.org/
        License: GPL-2.0
        Tags:
          git
          vcs
        Installer:
          Installer Type: inno
          Installer Url: https://github.com/git-for-windows/git/releases/download/Git-2.47.1-64-bit.exe
        """;

    [Fact]
    public void WinGet_show_output_yields_description_homepage_and_fields()
    {
        var details = PackageDetails.From(PackageDetailsText.Parse(WinGetShow, ["Description"]));

        Assert.Equal("Git for Windows focuses on offering a lightweight, native set of tools.\n\nIt brings the full feature set of the Git SCM to Windows.", details.Description);
        Assert.Equal(new Uri("https://gitforwindows.org/"), details.Homepage);
        Assert.Contains(details.Fields, f => f is { Name: "Publisher", Value: "The Git Development Community" });
        Assert.Contains(details.Fields, f => f is { Name: "Tags", Value: "git\nvcs" });
        Assert.Contains(details.Fields, f => f is { Name: "Installer Type", Value: "inno" });
        Assert.DoesNotContain(details.Fields, f => f.Name == "Description");
        Assert.DoesNotContain(details.Fields, f => f.Name.StartsWith("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Chocolatey_info_output_splits_pipes_and_skips_status_lines()
    {
        const string output = """
            Chocolatey v2.3.0
            git 2.47.1 [Approved]
             Title: Git | Published: 11/26/2024
             Package approved as a trusted package on Nov 26 2024 08:14:27.
             Number of Downloads: 24412396 | Downloads for this version: 100361
             Software Site: https://git-scm.com/
             Software License: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html
             Summary: Git (for Windows) - Fast, scalable, distributed revision control system
             Description: Git for Windows focuses on offering a lightweight, native set of tools.
            It brings the full feature set of the Git SCM to Windows.

            1 packages found.
            """;

        var details = ChocolateyProvider.ParsePackageDetails(output);

        Assert.Equal("Git for Windows focuses on offering a lightweight, native set of tools.\nIt brings the full feature set of the Git SCM to Windows.", details.Description);
        Assert.Equal(new Uri("https://git-scm.com/"), details.Homepage);
        Assert.Contains(details.Fields, f => f is { Name: "Title", Value: "Git" });
        Assert.Contains(details.Fields, f => f is { Name: "Published", Value: "11/26/2024" });
        Assert.Contains(details.Fields, f => f is { Name: "Downloads for this version", Value: "100361" });
        Assert.Contains(details.Fields, f => f.Name == "Summary");
        Assert.DoesNotContain(details.Fields, f => f.Value.Contains("approved as a trusted", StringComparison.Ordinal));
    }

    [Fact]
    public void Scoop_info_format_list_output_is_parsed()
    {
        const string output = """

            Name        : git
            Description : Distributed version control system
            Version     : 2.47.1
            Bucket      : main
            Website     : https://gitforwindows.org
            License     : GPL-2.0-only
            Updated at  : 11/26/2024 9:12:44 AM
            Binaries    : bin\git.exe | bin\sh.exe

            """;

        var details = PackageDetails.From(PackageDetailsText.Parse(output));

        Assert.Equal("Distributed version control system", details.Description);
        Assert.Equal(new Uri("https://gitforwindows.org"), details.Homepage);
        Assert.Contains(details.Fields, f => f is { Name: "Updated at", Value: "11/26/2024 9:12:44 AM" });
        Assert.Contains(details.Fields, f => f is { Name: "Binaries", Value: @"bin\git.exe | bin\sh.exe" });
    }

    [Fact]
    public void Npm_view_json_is_parsed_with_people_and_repository_objects()
    {
        const string json = """
            npm warn something
            {
              "name": "typescript",
              "description": "TypeScript is a language for application scale JavaScript development",
              "dist-tags": { "latest": "5.7.2", "next": "5.8.0-dev" },
              "version": "5.7.2",
              "homepage": "https://www.typescriptlang.org/",
              "license": "Apache-2.0",
              "author": { "name": "Microsoft Corp." },
              "repository": { "type": "git", "url": "git+https://github.com/microsoft/TypeScript.git" },
              "bugs": { "url": "https://github.com/microsoft/TypeScript/issues" },
              "keywords": ["TypeScript", "Microsoft"],
              "maintainers": [{ "name": "a" }, "b <b@example.com>"],
              "engines": { "node": ">=14.17" }
            }
            """;

        var details = NpmParsing.ParseView(json);

        Assert.StartsWith("TypeScript is a language", details.Description);
        Assert.Equal(new Uri("https://www.typescriptlang.org/"), details.Homepage);
        Assert.Contains(details.Fields, f => f is { Name: "Author", Value: "Microsoft Corp." });
        Assert.Contains(details.Fields, f => f is { Name: "Repository", Value: "https://github.com/microsoft/TypeScript.git" });
        Assert.Contains(details.Fields, f => f is { Name: "Issues", Value: "https://github.com/microsoft/TypeScript/issues" });
        Assert.Contains(details.Fields, f => f is { Name: "Keywords", Value: "TypeScript, Microsoft" });
        Assert.Contains(details.Fields, f => f is { Name: "Latest version", Value: "5.7.2" });
        Assert.Contains(details.Fields, f => f is { Name: "Node.js", Value: ">=14.17" });
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    public void Homepage_is_only_accepted_for_web_addresses(string value)
    {
        var details = PackageDetails.From([new("Homepage", value)]);
        Assert.Null(details.Homepage);
    }

    [Fact]
    public async Task WinGet_details_run_show_with_exact_id_and_source_and_reject_unsafe_ids()
    {
        var runner = new RecordingRunner(new ProcessResult(0, WinGetShow, "", false));
        var provider = new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, @"C:\winget.exe");

        var details = await provider.GetPackageDetailsAsync(new("winget", "Git.Git", "Git", "2.47.1", "winget"), TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        var args = Assert.Single(runner.Specs).Arguments;
        Assert.Equal(["show", "--id", "Git.Git", "--exact", "--source", "winget"], args.Take(6));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetPackageDetailsAsync(
            new("winget", "--override", "x", "1", null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetPackageDetailsAsync(
            new("winget", "Git.Git", "x", "1", "--source"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scoop_details_include_the_bucket()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "Name : git", "", false));
        var provider = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, @"C:\scoop.ps1", @"C:\pwsh.exe");

        await provider.GetPackageDetailsAsync(new("scoop", "git", "git", "2.0", "main"), TestContext.Current.CancellationToken);

        Assert.Equal(["info", "main/git"], Assert.Single(runner.Specs).Arguments.TakeLast(2));
    }

    [Fact]
    public async Task Service_reads_details_from_the_owning_manager_and_caches_them()
    {
        var provider = new DetailsProvider("winget");
        var service = new PackageSearchService(new ProviderRegistry([provider, new DetailsProvider("scoop")]), new NoSettings());
        var package = new PackageSearchResult("WINGET", "Git.Git", "Git", "2.0", "winget");

        var first = await service.GetDetailsAsync(package, TestContext.Current.CancellationToken);
        var second = await service.GetDetailsAsync(package, TestContext.Current.CancellationToken);
        service.Invalidate("winget");
        await service.GetDetailsAsync(package, TestContext.Current.CancellationToken);

        Assert.Equal("Git.Git", first!.Description);
        Assert.Same(first, second);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) { Specs.Add(spec); return Task.FromResult(result); }
    }

    private sealed class NoSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DetailsProvider(string id) : IUpdateProvider, IPackageSearchProvider
    {
        public int Calls { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false);
        public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderAvailability(ProviderHealth.Available, "1", null));
        public Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackageSearchResult>>([]);
        public Task<PackageDetails?> GetPackageDetailsAsync(PackageSearchResult package, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<PackageDetails?>(new PackageDetails(package.PackageId, null, []));
        }
        public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
