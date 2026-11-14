using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Versioning;
using UpdateForge.Providers.Linux;
using Xunit;

namespace UpdateForge.Core.Tests;

public class DebianVersionTests
{
    [Theory]
    [InlineData("1.0", "1.1", VersionOrder.Older)]
    [InlineData("1.0-1", "1.0-2", VersionOrder.Older)]
    [InlineData("1.0~rc1", "1.0", VersionOrder.Older)]
    [InlineData("1.0", "1.0+dfsg", VersionOrder.Older)]
    [InlineData("1:0.9", "2.0", VersionOrder.Newer)]   // an epoch beats the upstream version
    [InlineData("5.3-4+b1", "5.3-4+b1", VersionOrder.Equal)]
    [InlineData("2.10", "2.9", VersionOrder.Newer)]    // numeric, not lexical
    [InlineData("1.0a", "1.0", VersionOrder.Newer)]
    [InlineData("9:1.0", "9:1.0-1", VersionOrder.Older)]
    public void Orders_like_dpkg(string a, string b, VersionOrder expected) => Assert.Equal(expected, DebianVersion.Compare(a, b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]      // upstream must start with a digit
    [InlineData("x:1.0")]    // epoch must be numeric
    public void Unparsable_is_unknown(string? bad) => Assert.Equal(VersionOrder.Unknown, DebianVersion.Compare(bad, "1.0"));
}

public class AptTests
{
    private const string Dpkg = "bash\t5.3-4+b1\tamd64\tii \tGNU Bourne Again SHell\n"
        + "libssl3t64\t3.5.1-1\tamd64\tii \tSecure Sockets Layer toolkit - shared libraries\n"
        + "htop\t3.4.1-1\tamd64\tii \tinteractive processes viewer\n"
        + "removed-pkg\t1.0\tamd64\trc \tleft over config\n"
        + "vim\t2:9.1.1-1\tamd64\tii \tVi IMproved\n";

    private const string Upgradable = "Listing...\n"
        + "libssl3t64/stable-security 3.5.2-1 amd64 [upgradable from: 3.5.1-1]\n"
        + "vim/stable 2:9.1.2-1 amd64 [upgradable from: 2:9.1.1-1]\n"
        + "htop/stable 3.4.1-1 amd64 [upgradable from: 3.4.1-1]\n";

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner(Func<ProcessSpec, ProcessResult>? install = null) => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        if (args.StartsWith("-W -f=${Package}", StringComparison.Ordinal)) return Ok(Dpkg);
        if (args == "showmanual") return Ok("bash\nvim\nhtop\n");
        if (args == "showhold") return Ok("htop\n");
        if (args == "list --upgradable") return Ok(Upgradable);
        return install?.Invoke(s) ?? Ok("apt 3.1.0 (amd64)");
    });

    private static AptProvider Provider(ScriptedRunner runner, bool root = true, bool reboot = false) =>
        new(runner, NullLogger<AptProvider>.Instance, "/fake/bin", () => root, () => reboot);

    [Fact]
    public void Parsers_skip_removed_packages_and_noise()
    {
        Assert.Equal(["bash", "libssl3t64", "htop", "vim"], AptParsing.ParseInstalled(Dpkg).Select(p => p.Name));
        Assert.Equal(3, AptParsing.ParseUpgradable(Upgradable).Count);
        Assert.Equal("2:9.1.2-1", AptParsing.ParseUpgradable(Upgradable).Single(u => u.Name == "vim").Candidate);
        Assert.Equal(["bash-builtins"], AptParsing.ParseSearch("bash-builtins - Bash loadable builtins\n--bad - x\n-oops").Select(r => r.Name));
        Assert.Equal("5.3-4", AptParsing.ParseCandidates("bash:\n  Installed: 5.3-4\n  Candidate: 5.3-4\nzzz:\n  Candidate: (none)\n")["bash"]);
    }

    [Fact]
    public async Task Inventory_is_manual_plus_upgradable_and_classifies_holds_and_updates()
    {
        var apps = await Provider(Runner(), root: false).DiscoverAsync(CancellationToken.None);
        Assert.Equal(["bash", "htop", "libssl3t64", "vim"], apps.Select(a => a.Name).Order());   // libssl is not manual but is upgradable
        var vim = apps.Single(a => a.Name == "vim");
        Assert.True(vim.UpdateAvailable);
        Assert.Equal("2:9.1.2-1", vim.AvailableVersion);
        Assert.True(vim.RequiresElevation);              // not root
        Assert.Equal("stable", vim.PackageSource);
        var htop = apps.Single(a => a.Name == "htop");
        Assert.True(htop.IsPinned);
        Assert.False(htop.UpdateAvailable);              // same version: not newer
        Assert.False(apps.Single(a => a.Name == "bash").UpdateAvailable);
    }

    [Fact]
    public async Task Root_does_not_need_elevation()
    {
        var apps = await Provider(Runner(), root: true).DiscoverAsync(CancellationToken.None);
        Assert.All(apps, a => Assert.False(a.RequiresElevation));
    }

    [Fact]
    public async Task Failed_package_query_is_an_error_not_an_empty_list()
    {
        var runner = new ScriptedRunner(s => s.Arguments.Contains("list") ? new ProcessResult(1, "", "boom", false) : Ok(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_as_root_runs_apt_get_directly_with_the_exact_version()
    {
        var runner = Runner(_ => Ok("done"));
        var app = (await Provider(runner).DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "vim");
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        var call = runner.Calls.Last();
        Assert.Equal(Path.Combine("/fake/bin", "apt-get"), call.FileName);
        Assert.Contains("vim:amd64=2:9.1.2-1", call.Arguments);
        Assert.Contains("--only-upgrade", call.Arguments);
        Assert.Equal("noninteractive", call.Environment!["DEBIAN_FRONTEND"]);
    }

    [Fact]
    public async Task Update_as_user_goes_through_pkexec_and_env()
    {
        var runner = Runner(_ => Ok("done"));
        var app = (await Provider(runner, root: false).DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "vim");
        await Provider(runner, root: false).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        var call = runner.Calls.Last();
        Assert.Equal(Path.Combine("/fake/bin", "pkexec"), call.FileName);
        Assert.Equal(Path.Combine("/fake/bin", "env"), call.Arguments[0]);
        Assert.Contains(Path.Combine("/fake/bin", "apt-get"), call.Arguments);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", call.Arguments);
    }

    [Theory]
    [InlineData("vim; rm -rf /")]
    [InlineData("-oAPT::Foo")]
    [InlineData("Vim")]
    public async Task Invalid_package_names_never_reach_the_command_line(string name)
    {
        var runner = Runner();
        var app = new InstalledApplication { Id = "apt:x", Name = name, ProviderId = "apt", PackageId = name, AvailableVersion = "1.0" };
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Invalid_version_is_refused()
    {
        var runner = Runner();
        var app = new InstalledApplication { Id = "apt:x", Name = "vim", ProviderId = "apt", PackageId = "vim", AvailableVersion = "1.0 --force-yes" };
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Held_package_is_blocked()
    {
        var runner = Runner();
        var app = (await Provider(runner).DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "htop");
        Assert.Equal(UpdateOutcome.Blocked, (await Provider(runner).UpdateAsync(new UpdateRequest(app) { }, new Progress<UpdateProgress>(), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(100, "E: Could not get lock /var/lib/dpkg/lock-frontend", ErrorCategory.InstallerRunning)]
    [InlineData(100, "E: Failed to fetch http://deb.debian.org/x  Temporary failure resolving 'deb.debian.org'", ErrorCategory.Network)]
    [InlineData(100, "E: Version '9.9' for 'vim' was not found", ErrorCategory.NoApplicableInstaller)]
    [InlineData(100, "E: something else", ErrorCategory.InstallerFailed)]
    public async Task Failures_are_categorized(int exit, string stderr, ErrorCategory expected)
    {
        var runner = Runner(_ => new ProcessResult(exit, "", stderr, false));
        var app = new InstalledApplication { Id = "apt:vim", Name = "vim", ProviderId = "apt", PackageId = "vim", AvailableVersion = "9.1.2-1" };
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task Dismissed_authorization_is_canceled_and_denied_is_manual()
    {
        var app = new InstalledApplication { Id = "apt:vim", Name = "vim", ProviderId = "apt", PackageId = "vim", AvailableVersion = "9.1.2-1" };
        var dismissed = await Provider(Runner(_ => new ProcessResult(126, "", "", false)), root: false).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Canceled, dismissed.Outcome);
        var denied = await Provider(Runner(_ => new ProcessResult(127, "", "Error executing command as another user: Not authorized", false)), root: false).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.ManualActionRequired, denied.Outcome);
        Assert.Equal(ErrorCategory.ElevationRequired, denied.Error);
    }

    [Fact]
    public async Task Reboot_marker_turns_success_into_restart_required()
    {
        var app = new InstalledApplication { Id = "apt:vim", Name = "vim", ProviderId = "apt", PackageId = "vim", AvailableVersion = "9.1.2-1" };
        var result = await Provider(Runner(_ => Ok("")), reboot: true).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.RestartRequired, result.Outcome);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public void Versions_compare_with_debian_rules() =>
        Assert.Equal(VersionOrder.Older, Provider(Runner()).CompareVersions("2:9.1.1-1", "2:9.1.2-1"));

    [Fact]
    public async Task Search_fills_in_candidate_versions()
    {
        var runner = new ScriptedRunner(s => s.Arguments.Contains("search") ? Ok("bash-doc - Documentation\nbash - GNU shell\n")
            : Ok("bash:\n  Installed: 5.3\n  Candidate: 5.3-4\nbash-doc:\n  Candidate: 5.3-4\n"));
        var found = await Provider(runner).SearchPackagesAsync("bash", CancellationToken.None);
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal("5.3-4", f.Version));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(runner).SearchPackagesAsync("-evil", CancellationToken.None));
    }
}

public class FlatpakTests
{
    private const string List = "com.valvesoftware.Steam\tSteam\t1.0.0.87\tstable\tflathub\tsystem\tx86_64\n"
        + "org.mozilla.firefox\tFirefox\t159.0\tstable\tflathub\tuser\tx86_64\n"
        + "org.example.NoVersion\tNo Version\t\tstable\tflathub\tuser\tx86_64\n"
        + "not valid id\tbad\t\t\t\tuser\tx86_64\n";

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner(Func<ProcessSpec, ProcessResult>? other = null) => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        if (args.StartsWith("list", StringComparison.Ordinal)) return Ok(List);
        if (args.Contains("remote-ls") && args.Contains("--user")) return Ok("org.mozilla.firefox\t160.0\tstable\tflathub\n");
        if (args.Contains("remote-ls")) return Ok("");
        return other?.Invoke(s) ?? Ok("Flatpak 1.18.4");
    });

    private static FlatpakProvider Provider(ScriptedRunner r, bool root = false) => new(r, NullLogger<FlatpakProvider>.Instance, "/fake/bin", () => root);

    [Fact]
    public async Task Updates_are_matched_to_their_own_installation()
    {
        var apps = await Provider(Runner()).DiscoverAsync(CancellationToken.None);
        Assert.Equal(3, apps.Count);                                                 // the malformed ID is dropped
        var fx = apps.Single(a => a.PackageId == "org.mozilla.firefox");
        Assert.True(fx.UpdateAvailable);
        Assert.Equal("160.0", fx.AvailableVersion);
        Assert.Equal(InstallScope.User, fx.Scope);
        Assert.False(fx.RequiresElevation);                                          // user installs never need polkit
        var steam = apps.Single(a => a.PackageId == "com.valvesoftware.Steam");
        Assert.False(steam.UpdateAvailable);
        Assert.Equal(InstallScope.Machine, steam.Scope);
        Assert.Equal("stable", apps.Single(a => a.PackageId == "org.example.NoVersion").InstalledVersion);   // falls back to the branch
    }

    [Fact]
    public async Task Update_targets_the_installation_it_came_from()
    {
        var runner = Runner(_ => Ok("ok"));
        var fx = (await Provider(runner).DiscoverAsync(CancellationToken.None)).Single(a => a.PackageId == "org.mozilla.firefox");
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(fx), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal(["update", "-y", "--noninteractive", "--app", "--user", "org.mozilla.firefox"], runner.Calls.Last().Arguments);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("org.example;rm")]
    [InlineData("noDots")]
    public async Task Invalid_ids_are_refused(string id)
    {
        var runner = Runner();
        var app = new InstalledApplication { Id = "flatpak:x", Name = id, ProviderId = "flatpak", PackageId = id, Scope = InstallScope.User };
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Install_needs_a_valid_remote()
    {
        var runner = Runner(_ => Ok("ok"));
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).InstallAsync(new InstallRequest("org.mozilla.firefox", "--user"), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Equal(UpdateOutcome.Succeeded, (await Provider(runner).InstallAsync(new InstallRequest("org.mozilla.firefox", "flathub"), new Progress<UpdateProgress>(), CancellationToken.None)).Outcome);
        Assert.Equal(["install", "-y", "--noninteractive", "flathub", "org.mozilla.firefox"], runner.Calls.Last().Arguments);
    }

    [Fact]
    public void Search_parses_tabs_and_empty_search_is_not_an_error()
    {
        var found = FlatpakParsing.ParseSearch("org.mozilla.firefox\tFirefox\t160.0\tstable\tflathub\n");
        Assert.Equal(("org.mozilla.firefox", "Firefox", "160.0", "flathub"), (found[0].PackageId, found[0].Name, found[0].Version, found[0].Source));
    }

    [Fact]
    public async Task No_matches_is_an_empty_result()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(1, "", "No matches found", false));
        Assert.Empty(await Provider(runner).SearchPackagesAsync("zzzz", CancellationToken.None));
    }

    [Fact]
    public async Task Unordered_but_changed_version_counts_as_progress()
    {
        var p = Provider(Runner());
        Assert.Equal(VersionOrder.Older, p.CompareVersions("r123", "r124"));
        Assert.Equal(VersionOrder.Equal, p.CompareVersions("r123", "r123"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_flatpak_is_not_installed_not_a_failure()
    {
        var p = new FlatpakProvider(Runner(), NullLogger<FlatpakProvider>.Instance, null, () => false);
        if (File.Exists("/usr/bin/flatpak") || File.Exists("/usr/local/bin/flatpak") || File.Exists("/bin/flatpak")) return;   // a real install on this machine
        Assert.Equal(ProviderHealth.NotInstalled, (await p.CheckAvailabilityAsync(CancellationToken.None)).Health);
    }
}

public class SnapTests
{
    private const string List = """
        Name               Version                     Rev    Tracking       Publisher    Notes
        bare               1.0                         5      latest/stable  canonical**  base
        core22             20260101                    2000   latest/stable  canonical**  base
        firefox            159.0-1                     9000   latest/stable  mozilla**    -
        vlc                3.0.20                      3500   latest/stable  videolan**   held
        snapd              2.78                        100    latest/stable  canonical**  snapd
        """;

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner(Func<ProcessSpec, ProcessResult>? other = null) => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        if (args == "list") return Ok(List);
        if (args == "refresh --list") return Ok("Name     Version   Rev   Size  Publisher  Notes\nfirefox  160.0-1   9100  300MB mozilla**  -\n");
        return other?.Invoke(s) ?? Ok("snap 2.78");
    });

    private static SnapProvider Provider(ScriptedRunner r, bool root = false) => new(r, NullLogger<SnapProvider>.Instance, "/fake/bin", () => root);

    [Fact]
    public async Task Base_snaps_are_hidden_and_held_and_updates_are_classified()
    {
        var apps = await Provider(Runner()).DiscoverAsync(CancellationToken.None);
        Assert.Equal(["firefox", "snapd", "vlc"], apps.Select(a => a.Name).Order());
        var fx = apps.Single(a => a.Name == "firefox");
        Assert.True(fx.UpdateAvailable);
        Assert.Equal("160.0-1", fx.AvailableVersion);
        Assert.Equal("mozilla", fx.Publisher);
        Assert.True(fx.RequiresElevation);
        Assert.True(apps.Single(a => a.Name == "vlc").IsPinned);
        Assert.False(apps.Single(a => a.Name == "snapd").UpdateAvailable);
    }

    [Fact]
    public async Task Nothing_to_refresh_message_is_not_data()
    {
        var runner = new ScriptedRunner(s => string.Join(' ', s.Arguments) == "list" ? Ok(List) : Ok(""));
        Assert.DoesNotContain(await Provider(runner).DiscoverAsync(CancellationToken.None), a => a.UpdateAvailable);
    }

    [Fact]
    public async Task Refresh_and_access_denied()
    {
        var runner = Runner(_ => Ok("firefox refreshed"));
        var fx = (await Provider(runner).DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "firefox");
        Assert.Equal(UpdateOutcome.Succeeded, (await Provider(runner).UpdateAsync(new UpdateRequest(fx), new Progress<UpdateProgress>(), CancellationToken.None)).Outcome);
        Assert.Equal(["refresh", "firefox"], runner.Calls.Last().Arguments);

        var denied = Runner(_ => new ProcessResult(1, "", "error: access denied (try with sudo)", false));
        var r = await Provider(denied).UpdateAsync(new UpdateRequest(fx), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.ManualActionRequired, r.Outcome);
        Assert.Equal(ErrorCategory.ElevationRequired, r.Error);
    }

    [Theory]
    [InlineData("--classic")]
    [InlineData("Firefox")]
    [InlineData("a;b")]
    public async Task Invalid_names_are_refused(string name)
    {
        var runner = Runner();
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).InstallAsync(new InstallRequest(name, null), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Find_output_parses()
    {
        var found = SnapParsing.ParseFind("Name  Version  Publisher  Notes  Summary\nfirefox  160.0-1  mozilla**  -  Mozilla Firefox web browser\n");
        Assert.Equal("firefox", found.Single().PackageId);
        Assert.Equal("160.0-1", found.Single().Version);
    }
}
