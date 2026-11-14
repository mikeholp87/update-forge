using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Providers.Linux;
using Xunit;

namespace UpdateForge.Core.Tests;

public class CargoTests
{
    // Captured from a real `cargo install --list` and `cargo search`, plus the path and git header shapes cargo documents.
    private const string InstallList = "cargo-audit v0.23.0:\n    cargo-audit\n"
        + "mytool v0.1.0 (/home/me/src/mytool):\n    mytool\n"
        + "gitty v2.0.0 (https://github.com/x/gitty#abc12345):\n    gitty\n"
        + "tauri-driver v2.1.0:\n    tauri-driver\n";

    private const string Search = "cargo-audit = \"0.23.1\"    # Audit Cargo.lock files\n"
        + "cargo-audit-fixer = \"0.1.0\"    # something else\n"
        + "... and 3 crates more (use --limit N to see more)\n"
        + "note: to learn more about a package, run `cargo info <name>`\n";

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner(Func<ProcessSpec, ProcessResult>? other = null) => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        if (args == "install --list") return Ok(InstallList);
        if (args.StartsWith("search cargo-audit ", StringComparison.Ordinal)) return Ok(Search);
        if (args.StartsWith("search tauri-driver ", StringComparison.Ordinal)) return Ok("tauri-driver = \"2.1.0\"    # driver\n");
        return other?.Invoke(s) ?? Ok("cargo 1.93.0 (abc 2026-01-01)");
    });

    private static CargoProvider Provider(ScriptedRunner r) => new(r, NullLogger<CargoProvider>.Instance, "/fake/bin");

    [Fact]
    public void Parses_registry_path_and_git_installs()
    {
        var list = CargoParsing.ParseInstallList(InstallList);
        Assert.Equal(["cargo-audit", "mytool", "gitty", "tauri-driver"], list.Select(c => c.Name));
        Assert.Null(list[0].Source);
        Assert.Equal("/home/me/src/mytool", list[1].Source);
        Assert.Equal("https://github.com/x/gitty#abc12345", list[2].Source);
        Assert.Equal(["cargo-audit", "cargo-audit-fixer"], CargoParsing.ParseSearch(Search).Select(c => c.Name));
    }

    [Fact]
    public async Task Inventory_uses_exact_crate_match_and_holds_path_and_git_installs()
    {
        var apps = await Provider(Runner()).DiscoverAsync(CancellationToken.None);
        var audit = apps.Single(a => a.Name == "cargo-audit");
        Assert.True(audit.UpdateAvailable);
        Assert.Equal("0.23.1", audit.AvailableVersion);
        Assert.Equal(InstallScope.User, audit.Scope);
        Assert.False(audit.RequiresElevation);
        Assert.False(apps.Single(a => a.Name == "tauri-driver").UpdateAvailable);
        Assert.True(apps.Single(a => a.Name == "mytool").IsPinned);
        Assert.True(apps.Single(a => a.Name == "gitty").IsPinned);
    }

    [Fact]
    public async Task Update_installs_the_exact_version_that_was_checked()
    {
        var runner = Runner();
        var app = (await Provider(runner).DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "cargo-audit");
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal(["install", "cargo-audit", "--version", "=0.23.1"], runner.Calls.Last().Arguments);
        Assert.Equal(Path.Combine("/fake/bin", "cargo"), runner.Calls.Last().FileName);
    }

    [Fact]
    public async Task Missing_compiler_is_explained()
    {
        var runner = Runner(s => s.Arguments[0] == "install"
            ? new ProcessResult(101, "", "error: linker `cc` not found", false) : Ok("cargo 1.93.0"));
        var app = new InstalledApplication { Id = "cargo:x", Name = "x", ProviderId = "cargo", PackageId = "x", AvailableVersion = "1.0.0", PackageSource = "crates.io" };
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.InstallerFailed, result.Error);
        Assert.Contains("C compiler", result.Message);
    }

    [Theory]
    [InlineData("--git")]
    [InlineData("a b")]
    [InlineData("../x")]
    public async Task Invalid_crate_names_never_reach_the_command_line(string name)
    {
        var runner = Runner();
        var app = new InstalledApplication { Id = "cargo:x", Name = name, ProviderId = "cargo", PackageId = name, AvailableVersion = "1.0.0" };
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).InstallAsync(new InstallRequest(name, null), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Offline_lookups_fail_the_scan_instead_of_reporting_up_to_date()
    {
        var runner = new ScriptedRunner(s => s.Arguments[0] == "install" ? Ok(InstallList) : new ProcessResult(101, "", "error: failed to query", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void Parses_cargo_info()
    {
        const string info = "ripgrep #regex #grep\nripgrep is a line-oriented search tool that\nrecursively searches the current directory.\nversion: 15.2.0\nlicense: Unlicense OR MIT\nhomepage: https://github.com/BurntSushi/ripgrep\n";
        var d = CargoParsing.ParseInfo(info);
        Assert.Equal("ripgrep is a line-oriented search tool that recursively searches the current directory.", d.Description);
        Assert.Equal("https://github.com/BurntSushi/ripgrep", d.Homepage?.ToString());
        Assert.Contains(d.Fields, f => f.Name == "license");
    }
}

public class NodeToolTests
{
    private static ProcessResult Ok(string text) => new(0, text, "", false);

    [Fact]
    public async Task Pnpm_reports_outdated_packages_even_though_pnpm_exits_1()
    {
        const string ls = """[{"path":"/home/me/.local/share/pnpm/global/5","dependencies":{"typescript":{"from":"typescript","version":"5.4.0"},"@scope/tool":{"version":"1.0.0"},"mine":{"version":"link:/home/me/mine"}}}]""";
        const string outdated = """{"typescript":{"current":"5.4.0","latest":"5.9.2","wanted":"5.4.0","isDeprecated":false,"dependencyType":"dependencies"}}""";
        var runner = new ScriptedRunner(s => s.Arguments[0] == "ls" ? Ok(ls)
            : s.Arguments[0] == "outdated" ? new ProcessResult(1, outdated, "", false) : Ok("10.5.0"));
        var apps = await new PnpmProvider(runner, NullLogger<PnpmProvider>.Instance, "/fake/bin").DiscoverAsync(CancellationToken.None);

        Assert.Equal(["@scope/tool", "mine", "typescript"], apps.Select(a => a.Name).Order());
        var ts = apps.Single(a => a.Name == "typescript");
        Assert.True(ts.UpdateAvailable);
        Assert.Equal("5.9.2", ts.AvailableVersion);
        Assert.False(apps.Single(a => a.Name == "@scope/tool").UpdateAvailable);
        Assert.True(apps.Single(a => a.Name == "mine").IsPinned);

        var update = await new PnpmProvider(runner, NullLogger<PnpmProvider>.Instance, "/fake/bin").UpdateAsync(new UpdateRequest(ts), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["add", "-g", "typescript@5.9.2"], runner.Calls.Last().Arguments);
        Assert.Equal(UpdateOutcome.Succeeded, update.Outcome);
        Assert.Equal("0", runner.Calls.Last().Environment!["COREPACK_ENABLE_DOWNLOAD_PROMPT"]);
    }

    [Fact]
    public async Task Pnpm_failure_with_no_json_is_a_failure_not_up_to_date()
    {
        var runner = new ScriptedRunner(s => s.Arguments[0] == "ls" ? Ok("""[{"dependencies":{"a":{"version":"1.0.0"}}}]""") : new ProcessResult(1, "", "ERR_PNPM_META_FETCH_FAIL", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PnpmProvider(runner, NullLogger<PnpmProvider>.Instance, "/fake/bin").DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public void Bun_list_handles_scoped_names_and_ignores_the_heading()
    {
        // The tree is captured from a real `bun pm ls -g`.
        const string output = "/home/me/.bun/install/global node_modules (2 installed)\n├── @anthropic-ai/claude-code@2.0.1\n└── left-pad@1.1.0\n";
        Assert.Equal([("@anthropic-ai/claude-code", "2.0.1"), ("left-pad", "1.1.0")], NodeToolParsing.ParseBunList(output));
    }

    [Fact]
    public async Task Bun_looks_each_package_up_with_bun_info()
    {
        var runner = new ScriptedRunner(s =>
        {
            var args = string.Join(' ', s.Arguments);
            return args == "pm ls -g" ? Ok("/x node_modules (2 installed)\n├── left-pad@1.1.0\n└── fresh@2.0.0\n")
                : args == "info left-pad version" ? Ok("1.3.0\n")
                : args == "info fresh version" ? Ok("2.0.0\n") : Ok("1.4.4");
        });
        var provider = new BunProvider(runner, NullLogger<BunProvider>.Instance, "/fake/bin");
        var apps = await provider.DiscoverAsync(CancellationToken.None);
        Assert.Equal("1.3.0", apps.Single(a => a.Name == "left-pad").AvailableVersion);
        Assert.False(apps.Single(a => a.Name == "fresh").UpdateAvailable);

        await provider.UpdateAsync(new UpdateRequest(apps.Single(a => a.Name == "left-pad")), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["add", "-g", "left-pad@1.3.0"], runner.Calls.Last().Arguments);
    }

    [Fact]
    public async Task Yarn_reads_the_global_folder_because_yarn_global_list_hides_packages_without_binaries()
    {
        var dir = Directory.CreateTempSubdirectory("uf-yarn").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), """{"dependencies":{"left-pad":"^1.1.0","gone":"1.0.0"}}""");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "left-pad"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "left-pad", "package.json"), """{"name":"left-pad","version":"1.1.0"}""");

            var runner = new ScriptedRunner(s =>
            {
                var args = string.Join(' ', s.Arguments);
                return args == "global dir" ? Ok(dir + "\n")
                    : args == "info left-pad version --json" ? Ok("""{"type":"inspect","data":"1.3.0"}""" + "\n")
                    : Ok("1.22.22");
            });
            var provider = new YarnProvider(runner, NullLogger<YarnProvider>.Instance, "/fake/bin");
            var apps = await provider.DiscoverAsync(CancellationToken.None);

            var pad = Assert.Single(apps);   // "gone" is declared but not installed
            Assert.Equal("left-pad", pad.Name);
            Assert.Equal("1.3.0", pad.AvailableVersion);
            await provider.UpdateAsync(new UpdateRequest(pad), new Progress<UpdateProgress>(), CancellationToken.None);
            Assert.Equal(["global", "add", "left-pad@1.3.0"], runner.Calls.Last().Arguments);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Yarn_info_ignores_warning_lines()
    {
        Assert.Equal("1.3.0", NodeToolParsing.ParseYarnInfo("warning something\n{\"type\":\"warning\",\"data\":\"x\"}\n{\"type\":\"inspect\",\"data\":\"1.3.0\"}\n"));
        Assert.Null(NodeToolParsing.ParseYarnInfo("error nope\n"));
    }

    [Fact]
    public async Task Yarn_berry_is_reported_unavailable()
    {
        var runner = new ScriptedRunner(_ => Ok("4.9.1"));
        var availability = await new YarnProvider(runner, NullLogger<YarnProvider>.Instance, "/fake/bin").CheckAvailabilityAsync(CancellationToken.None);
        Assert.False(availability.IsAvailable);
        Assert.Contains("Classic", availability.Message);
    }

    [Theory]
    [InlineData("--registry=evil")]
    [InlineData("pkg name")]
    [InlineData("pkg;rm")]
    public async Task Invalid_package_names_never_reach_any_node_tool_command_line(string name)
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var app = new InstalledApplication { Id = "x", Name = name, ProviderId = "pnpm", PackageId = name, AvailableVersion = "1.0.0" };
        Assert.Equal(ErrorCategory.SecurityBlocked, (await new PnpmProvider(runner, NullLogger<PnpmProvider>.Instance, "/fake/bin").UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Equal(ErrorCategory.SecurityBlocked, (await new BunProvider(runner, NullLogger<BunProvider>.Instance, "/fake/bin").InstallAsync(new InstallRequest(name, null), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Equal(ErrorCategory.SecurityBlocked, (await new YarnProvider(runner, NullLogger<YarnProvider>.Instance, "/fake/bin").InstallAsync(new InstallRequest(name, null), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Version_arguments_are_validated_too()
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        var result = await new BunProvider(runner, NullLogger<BunProvider>.Instance, "/fake/bin")
            .InstallAsync(new InstallRequest("left-pad", null, "1.0.0 --global"), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.SecurityBlocked, result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Permission_errors_need_the_user_not_root()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(1, "", "EACCES: permission denied, mkdir '/usr/lib/node_modules'", false));
        var result = await new BunProvider(runner, NullLogger<BunProvider>.Instance, "/fake/bin")
            .InstallAsync(new InstallRequest("left-pad", null), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(UpdateOutcome.ManualActionRequired, result.Outcome);
        Assert.Equal(ErrorCategory.ElevationRequired, result.Error);
    }
}

public class PipxTests
{
    // Shape of `pipx list --json` (pipx_spec_version 0.1) and `pip list --outdated --format=json`.
    private const string List = """
        {"pipx_spec_version":"0.1","venvs":{
          "black":{"metadata":{"main_package":{"package":"black","package_version":"24.1.0"}}},
          "yt-dlp":{"metadata":{"main_package":{"package":"yt-dlp","package_version":"2026.1.1"}}},
          "broken":{"metadata":{}}}}
        """;

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner() => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        if (args == "list --json") return Ok(List);
        if (args == "runpip black list --outdated --format=json") return Ok("""[{"name":"black","version":"24.1.0","latest_version":"25.1.0","latest_filetype":"wheel"},{"name":"click","version":"8.0","latest_version":"8.2","latest_filetype":"wheel"}]""");
        if (args == "runpip yt-dlp list --outdated --format=json") return Ok("[]");
        return Ok("1.7.1");
    });

    [Fact]
    public async Task Each_app_is_checked_with_its_own_pip_and_dependencies_do_not_count()
    {
        var apps = await new PipxProvider(Runner(), NullLogger<PipxProvider>.Instance, "/fake/bin").DiscoverAsync(CancellationToken.None);
        Assert.Equal(["black", "yt-dlp"], apps.Select(a => a.Name).Order());   // "broken" has no version
        Assert.Equal("25.1.0", apps.Single(a => a.Name == "black").AvailableVersion);
        Assert.False(apps.Single(a => a.Name == "yt-dlp").UpdateAvailable);
    }

    [Fact]
    public async Task Update_upgrades_the_app_and_cannot_pin_a_version()
    {
        var runner = Runner();
        var provider = new PipxProvider(runner, NullLogger<PipxProvider>.Instance, "/fake/bin");
        var black = (await provider.DiscoverAsync(CancellationToken.None)).Single(a => a.Name == "black");
        await provider.UpdateAsync(new UpdateRequest(black), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["upgrade", "black"], runner.Calls.Last().Arguments);

        var pinned = await provider.InstallAsync(new InstallRequest("black", null, "24.1.0"), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(ErrorCategory.NoApplicableInstaller, pinned.Error);
        await provider.InstallAsync(new InstallRequest("ruff", null), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["install", "ruff"], runner.Calls.Last().Arguments);
    }

    [Theory]
    [InlineData("pip name", false)]
    [InlineData("--user", false)]
    [InlineData("git+https://x/y", false)]
    [InlineData("yt-dlp", true)]
    [InlineData("Black_2", true)]
    public void Name_validation(string name, bool valid) =>
        Assert.Equal(valid, new PipxProvider(Runner(), NullLogger<PipxProvider>.Instance, "/fake/bin").IsValidName(name));

    [Fact]
    public void Normalizes_names_like_pep_503() => Assert.Equal("my-pkg", PipxParsing.Normalize("My_Pkg"));
}

public class HomebrewTests
{
    // Version rows are captured from a real `brew list --versions`; the JSON shapes are those of `brew outdated --json=v2` and `brew info --json=v2`.
    private const string Versions = "abseil 20260817.0\nada-url 4.0.0_1\nripgrep 15.1.0\nbat 0.25.0 0.26.0\nzlib 1.3.1\n";
    private const string Outdated = """{"formulae":[{"name":"ripgrep","installed_versions":["15.1.0"],"current_version":"15.2.0","pinned":false},{"name":"zlib","installed_versions":["1.3.1"],"current_version":"1.3.2","pinned":false}],"casks":[]}""";

    private static ProcessResult Ok(string text) => new(0, text, "", false);

    private static ScriptedRunner Runner(Func<ProcessSpec, ProcessResult>? other = null) => new(s =>
    {
        var args = string.Join(' ', s.Arguments);
        return args switch
        {
            "list --formula --versions" => Ok(Versions),
            "list --cask --versions" => Ok("firefox 140.0\n"),
            "leaves" => Ok("bat\nripgrep\n"),
            "list --pinned" => Ok("bat\n"),
            "outdated --json=v2" => Ok(Outdated),
            _ => other?.Invoke(s) ?? Ok("Homebrew 5.0.0"),
        };
    });

    private static HomebrewProvider Provider(ScriptedRunner r) => new(r, NullLogger<HomebrewProvider>.Instance, "/fake/bin");

    [Fact]
    public void Parses_versions_and_takes_the_last_kept_version()
    {
        var map = HomebrewParsing.ParseVersions(Versions);
        Assert.Equal("4.0.0_1", map["ada-url"]);
        Assert.Equal("0.26.0", map["bat"]);
        Assert.Equal(["a", "b@3.12"], HomebrewParsing.ParseNames("a\n==> Casks\nb@3.12\n--bad\n"));
    }

    [Fact]
    public async Task Inventory_is_installed_on_request_plus_outdated_plus_casks_and_honors_pins()
    {
        var apps = await Provider(Runner()).DiscoverAsync(CancellationToken.None);
        // abseil and ada-url are dependencies that are current, so they are hidden; zlib is a dependency but outdated.
        Assert.Equal(["bat", "firefox", "ripgrep", "zlib"], apps.Select(a => a.Name).Order());
        var rg = apps.Single(a => a.Name == "ripgrep");
        Assert.True(rg.UpdateAvailable);
        Assert.Equal("15.2.0", rg.AvailableVersion);
        Assert.Equal("formula", rg.PackageSource);
        Assert.True(apps.Single(a => a.Name == "bat").IsPinned);
        var fx = apps.Single(a => a.Name == "firefox");
        Assert.Equal("brew:cask:firefox", fx.Id);
        Assert.Equal("cask", fx.PackageSource);
    }

    [Fact]
    public async Task Update_and_install_name_the_kind_explicitly()
    {
        var runner = Runner();
        var provider = Provider(runner);
        var apps = await provider.DiscoverAsync(CancellationToken.None);
        await provider.UpdateAsync(new UpdateRequest(apps.Single(a => a.Name == "ripgrep")), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["upgrade", "--formula", "ripgrep"], runner.Calls.Last().Arguments);
        await provider.InstallAsync(new InstallRequest("firefox", "cask"), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Equal(["install", "--cask", "firefox"], runner.Calls.Last().Arguments);
        Assert.Equal("1", runner.Calls.Last().Environment!["HOMEBREW_NO_ANALYTICS"]);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("--cask")]
    [InlineData("./local.rb")]
    [InlineData("https://x/y.rb")]
    [InlineData("a/b/c/d")]
    public async Task Invalid_names_and_unknown_sources_never_reach_brew(string name)
    {
        var runner = Runner();
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).InstallAsync(new InstallRequest(name, null), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Equal(ErrorCategory.SecurityBlocked, (await Provider(runner).InstallAsync(new InstallRequest("ripgrep", "--force"), new Progress<UpdateProgress>(), CancellationToken.None)).Error);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("homebrew/cask/firefox", true)]
    [InlineData("python@3.12", true)]
    [InlineData("gcc", true)]
    [InlineData("-x", false)]
    public void Tap_qualified_and_versioned_names_are_valid(string name, bool valid) =>
        Assert.Equal(valid, HomebrewProvider.IsValidBrewName(name));

    [Fact]
    public async Task Running_as_root_is_explained()
    {
        var runner = Runner(s => s.Arguments[0] == "upgrade" ? new ProcessResult(1, "", "Error: Running Homebrew as root is extremely dangerous", false) : Ok("Homebrew 5.0.0"));
        var app = new InstalledApplication { Id = "brew:x", Name = "x", ProviderId = "brew", PackageId = "x", PackageSource = "formula", AvailableVersion = "2.0" };
        var result = await Provider(runner).UpdateAsync(new UpdateRequest(app), new Progress<UpdateProgress>(), CancellationToken.None);
        Assert.Contains("normal user", result.Message);
    }

    [Fact]
    public async Task Search_finds_formulae_and_casks_and_treats_no_match_as_empty()
    {
        var runner = Runner(s => string.Join(' ', s.Arguments) switch
        {
            "search --formula -- rip" => Ok("ripgrep\nripgrep-all\n"),
            "search --cask -- rip" => new ProcessResult(1, "", "Error: No casks found for \"rip\".", false),
            _ => Ok(""),
        });
        var results = await Provider(runner).SearchPackagesAsync("rip", CancellationToken.None);
        Assert.Equal(["ripgrep", "ripgrep-all"], results.Select(r => r.PackageId));
        Assert.All(results, r => Assert.Equal("formula", r.Source));
    }

    [Fact]
    public void Parses_info_json()
    {
        const string json = """{"formulae":[{"desc":"Search tool like grep","homepage":"https://github.com/BurntSushi/ripgrep","license":"Unlicense","versions":{"stable":"15.2.0"},"tap":"homebrew/core","dependencies":["pcre2"]}],"casks":[]}""";
        var d = HomebrewParsing.ParseInfo(json);
        Assert.Equal("Search tool like grep", d.Description);
        Assert.Equal("https://github.com/BurntSushi/ripgrep", d.Homepage?.ToString());
        Assert.Contains(d.Fields, f => f is { Name: "Latest version", Value: "15.2.0" });
    }

    [Fact]
    public async Task Refresh_runs_brew_update() 
    {
        var runner = Runner(s => Ok("Already up-to-date."));
        Assert.True(Provider(runner).CanRefreshSources);
        await Provider(runner).RefreshSourcesAsync(CancellationToken.None);
        Assert.Equal(["update"], runner.Calls.Last().Arguments);
    }
}
