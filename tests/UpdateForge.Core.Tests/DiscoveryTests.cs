using System.Net;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Recommendations;
using UpdateForge.Providers.Linux;
using Xunit;

namespace UpdateForge.Core.Tests;

public class DiscoveryParsingTests
{
    [Fact]
    public void A_desktop_entry_gives_its_name_and_categories()
    {
        var e = DesktopEntryParsing.Parse("# comment\n[Desktop Entry]\nType=Application\nName=Inkscape\nName[de]=Tinte\nCategories=Graphics;VectorGraphics;GTK;\nExec=inkscape\n[Desktop Action new]\nName=New\n");
        Assert.NotNull(e);
        Assert.Equal("Inkscape", e!.Name);
        Assert.Equal(["Graphics", "VectorGraphics", "GTK"], e.Categories);
    }

    [Theory]
    [InlineData("[Desktop Entry]\nType=Application\nName=Hidden thing\nNoDisplay=true\n")]
    [InlineData("[Desktop Entry]\nType=Application\nName=Gone\nHidden=true\n")]
    [InlineData("[Desktop Entry]\nType=Link\nName=A link\n")]
    [InlineData("[Desktop Entry]\nType=Application\n")]
    [InlineData("[Other]\nType=Application\nName=Wrong group\n")]
    public void Hidden_nameless_and_non_application_entries_are_skipped(string text) => Assert.Null(DesktopEntryParsing.Parse(text));

    [Fact]
    public void A_dpkg_log_gives_the_latest_install_per_package()
    {
        var log = "2026-11-01 09:00:00 install steam:i386 <none> 1.0\n2026-11-01 09:00:05 status installed steam:i386 1.0\n"
            + "2026-11-10 10:30:00 install steam:i386 <none> 1.1\n2026-10-02 08:00:00 install htop:amd64 <none> 3.4\n2026-10-03 08:00:00 upgrade vim:amd64 1 2\n";
        var map = DesktopEntryParsing.ParseDpkgLog(log);
        Assert.Equal(2, map.Count);
        Assert.Equal(new DateTime(2026, 11, 10, 10, 30, 0), map["steam"].LocalDateTime);
        Assert.True(map.ContainsKey("htop"));
    }

    [Fact]
    public void Dependency_fields_lose_versions_and_architectures_but_keep_alternatives()
    {
        Assert.Equal(["a", "b", "c", "d"], AptRelations.ParseNames("a (>= 1.0), b | c:any, d <!nocheck>"));
        Assert.Empty(AptRelations.ParseNames(null));
        Assert.Empty(AptRelations.ParseNames("Bad_Name!"));
    }

    [Fact]
    public void Installed_rows_keep_only_fully_installed_packages_with_their_relations()
    {
        var rows = AptRelations.ParseInstalled("git\tii \tgit-man\tgitk, git-doc\nold\trc \t\t\nvlc\tii \t\tvlc-plugin-access-extra\n");
        Assert.Equal(["git", "vlc"], rows.Select(r => r.Name));
        Assert.Equal(["git-man"], rows[0].Recommends);
        Assert.Equal(["gitk", "git-doc"], rows[0].Suggests);
    }

    private const string Show = "Package: gitk\nSection: vcs\nDescription: fast, portable git history browser\n and more\nTag: role::program, uitoolkit::tk,\n works-with::vcs\n\n"
        + "Package: git-doc\nSection: doc\nDescription: documentation\nTag: role::documentation\n\n"
        + "Package: libfoo3\nSection: libs\nDescription: library\n\n"
        + "Package: cowsay\nSection: games\nDescription: configurable talking cow\n";

    [Fact]
    public void Apt_show_stanzas_parse_with_folded_tags()
    {
        var shown = AptRelations.ParseShow(Show);
        Assert.Equal(["gitk", "git-doc", "libfoo3", "cowsay"], shown.Select(s => s.Name));
        Assert.Equal("fast, portable git history browser", shown[0].Summary);
        Assert.Contains("works-with::vcs", shown[0].Tags);
    }

    [Fact]
    public void Only_applications_are_worth_suggesting()
    {
        var shown = AptRelations.ParseShow(Show).ToDictionary(s => s.Name);
        Assert.True(AptRelations.LooksLikeApplication(shown["gitk"]));
        Assert.True(AptRelations.LooksLikeApplication(shown["cowsay"]));   // no tags: the section decides
        Assert.False(AptRelations.LooksLikeApplication(shown["git-doc"]));
        Assert.False(AptRelations.LooksLikeApplication(shown["libfoo3"]));
    }

    private const string FlathubJson = """
        {"hits":[
          {"app_id":"org.good.App","name":"Good App","summary":"Does good\u001b[31m things","type":"desktop-application","is_free_license":true,"installs_last_month":5000},
          {"app_id":"org.closed.App","name":"Closed","summary":"x","type":"desktop-application","is_free_license":false,"installs_last_month":9000},
          {"app_id":"org.tiny.App","name":"Tiny","summary":"x","type":"desktop-application","is_free_license":true,"installs_last_month":10},
          {"app_id":"org.rt.Runtime","name":"Runtime","summary":"x","type":"runtime","is_free_license":true,"installs_last_month":99999},
          {"app_id":"bad id; rm -rf","name":"Evil","summary":"x","type":"desktop-application","is_free_license":true,"installs_last_month":99999}
        ]}
        """;

    [Fact]
    public void Flathub_hits_are_filtered_and_cleaned()
    {
        var found = FlathubParsing.ParseCategory(FlathubJson, "gaming", 12);
        var only = Assert.Single(found);
        Assert.Equal("org.good.App", only.PackageId);
        Assert.Equal("flatpak", only.ProviderId);
        Assert.Equal("flathub", only.Source);
        Assert.Equal(DiscoveryOrigin.Online, only.Origin);
        Assert.DoesNotContain('\u001b', only.Summary);
    }

    [Fact]
    public void Garbage_json_gives_nothing() => Assert.Empty(FlathubParsing.ParseCategory("<html>nope", "gaming", 5));

    [Fact]
    public void Text_from_outside_loses_control_characters_and_is_capped()
    {
        Assert.Equal("a b", DiscoveryText.Clean("a‮\n  b\u0007", 50));
        Assert.Equal(5, DiscoveryText.Clean(new string('x', 100), 5).Length);
        Assert.Equal("", DiscoveryText.Clean(null, 5));
    }

    [Fact]
    public void Category_vocabularies_map_to_niches()
    {
        Assert.Equal(["creative"], CategoryNiches.FromDesktopCategories(["Graphics", "RasterGraphics", "GTK"]));
        Assert.Equal("gaming", CategoryNiches.FromAptSection("contrib/games"));
        Assert.Null(CategoryNiches.FromAptSection("libs"));
        Assert.Equal("Game", CategoryNiches.FlathubCategory("gaming"));
        Assert.Null(CategoryNiches.FlathubCategory("backup"));
    }
}

public class DiscoverySourceTests
{
    private static ProcessResult Ok(string text) => new(0, text, "", false);

    [Fact]
    public async Task Apt_discovery_suggests_what_your_chosen_packages_recommend_and_skips_the_rest()
    {
        var runner = new ScriptedRunner(s =>
        {
            var args = string.Join(' ', s.Arguments);
            if (args == "showmanual") return Ok("git\nvlc\nlibfoo3\n");
            if (args.StartsWith("-W -f=", StringComparison.Ordinal))
                return Ok("git\tii \tgit-man\tgitk, git-doc\nvlc\tii \tlibfoo3\t\ngit-man\tii \t\tgitk\nlibfoo3\tii \t\tpulseaudio\nautodep\tii \t\tcowsay\n");
            if (args.StartsWith("show --no-all-versions -- ", StringComparison.Ordinal))
                return Ok("Package: gitk\nSection: vcs\nDescription: history browser\n\nPackage: git-doc\nSection: doc\nDescription: docs\nTag: role::documentation\n\nPackage: cowsay\nSection: games\nDescription: cow\n\nPackage: pulseaudio\nSection: sound\nDescription: sound server\nTag: interface::daemon, role::program\n");
            return new ProcessResult(1, "", "unexpected " + args, false);
        });
        var found = await new AptRelationDiscovery(runner, "/fake/bin").DiscoverAsync(new DiscoveryRequest([], []), TestContext.Current.CancellationToken);

        var gitk = Assert.Single(found);                            // git-doc is documentation; cowsay is only suggested by an auto-installed package
        Assert.Equal("gitk", gitk.PackageId);
        Assert.Equal("development", gitk.Niche);
        Assert.Equal(["git"], gitk.RelatedTo);                       // git-man was not chosen by the user
        Assert.Equal(DiscoveryOrigin.Related, gitk.Origin);
        Assert.DoesNotContain(runner.Calls, c => c.FileName.EndsWith("apt-get", StringComparison.Ordinal));   // read-only
    }

    [Fact]
    public async Task Apt_discovery_gives_nothing_when_a_tool_fails()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(100, "", "E: broken", false));
        Assert.Empty(await new AptRelationDiscovery(runner, "/fake/bin").DiscoverAsync(new DiscoveryRequest([], []), TestContext.Current.CancellationToken));
    }

    private sealed class FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "uf-flathub-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Flathub_discovery_sends_only_a_category_and_caches_the_answer()
    {
        var dir = TempDir();
        try
        {
            var handler = new FakeHandler("""{"hits":[{"app_id":"org.good.App","name":"Good","summary":"s","type":"desktop-application","is_free_license":true,"installs_last_month":5000}]}""");
            var source = new FlathubDiscovery(new HttpClient(handler), dir, null);
            var installed = new InstalledApplication { Id = "apt:secret-internal-tool", Name = "secret-internal-tool", ProviderId = "apt" };
            var request = new DiscoveryRequest([installed], ["gaming", "backup"]);

            Assert.Single(await source.DiscoverAsync(request, TestContext.Current.CancellationToken));
            Assert.Single(await source.DiscoverAsync(request, TestContext.Current.CancellationToken));

            var uri = Assert.Single(handler.Requests);                // backup has no Flathub category; the second call came from the cache
            Assert.Equal("flathub.org", uri.Host);
            Assert.Equal("/api/v2/collection/category/Game", uri.AbsolutePath);
            Assert.DoesNotContain("secret", uri.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Flathub_discovery_gives_nothing_on_a_server_error()
    {
        var dir = TempDir();
        try
        {
            var source = new FlathubDiscovery(new HttpClient(new FakeHandler("oops", HttpStatusCode.InternalServerError)), dir, null);
            Assert.Empty(await source.DiscoverAsync(new DiscoveryRequest([], ["gaming"]), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task System_signals_read_launchers_and_install_history()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.desktop"), "[Desktop Entry]\nType=Application\nName=Krita\nCategories=Graphics;\n");
            File.WriteAllText(Path.Combine(dir, "b.desktop"), "[Desktop Entry]\nType=Application\nName=Hidden\nNoDisplay=true\n");
            var log = Path.Combine(dir, "dpkg.log");
            File.WriteAllText(log, "2026-11-10 10:30:00 install krita:amd64 <none> 5.2\n");
            var signals = await new LinuxSystemSignals([dir, Path.Combine(dir, "missing")], [log, Path.Combine(dir, "nolog")]).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["Krita"], signals.Launchers.Select(l => l.Name));
            Assert.True(signals.InstalledAt.ContainsKey("krita"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DiscoveryEngineTests
{
    private static readonly string[] Niches8 = ["development", "gaming", "media", "music", "creative", "office", "security", "backup"];

    private static InstalledApplication App(string name, string provider = "apt") => new() { Id = $"{provider}:{name}", Name = name, ProviderId = provider, PackageId = name };

    private static DiscoveredPackage Related(string id, string niche = "development", params string[] from) =>
        new("apt", id, id, "summary of " + id, niche, DiscoveryOrigin.Related, from.Length == 0 ? ["git"] : from);

    private static DiscoveredPackage Online(string id, string niche, long installs = 5000) =>
        new("flatpak", id, id, "online " + id, niche, DiscoveryOrigin.Online, [], "flathub", installs);

    private static RecommendationContext Ctx(IEnumerable<DiscoveredPackage> found, string[]? installed = null, string[]? providers = null, string[]? dismissed = null,
        InterestProfile? profile = null) => new()
    {
        Installed = [.. (installed ?? ["git", "cmake", "clang", "steam", "lutris", "heroic"]).Select(n => App(n))],
        InstallableProviders = (providers ?? ["apt", "flatpak"]).ToHashSet(),
        Dismissed = (dismissed ?? []).ToHashSet(),
        Discovered = [.. found],
        Profile = profile ?? new InterestProfile(),
    };

    [Fact]
    public void A_related_package_is_suggested_with_the_package_that_points_at_it()
    {
        var r = RecommendationEngine.Generate(Ctx([Related("gitk")])).Single(x => x.Key == "found:apt:gitk");
        Assert.Equal("Suggested by git, which you have installed", r.Because);
        Assert.Equal("apt", r.Package!.ProviderId);
        Assert.Equal(["development"], r.Niches);
    }

    [Fact]
    public void Nothing_is_suggested_twice_or_when_it_cannot_be_installed_or_was_dismissed()
    {
        var found = new[] { Related("steam"), Related("lazygit"), Related("gitk"), Related("meld") };   // steam installed; lazygit is a catalog entry
        var r = RecommendationEngine.Generate(Ctx(found, providers: ["apt"], dismissed: ["found:apt:meld"]));
        Assert.DoesNotContain(r, x => x.Key is "found:apt:steam" or "found:apt:lazygit" or "found:apt:meld");
        Assert.Contains(r, x => x.Key == "found:apt:gitk");
        Assert.DoesNotContain(RecommendationEngine.Generate(Ctx([Related("gitk")], providers: ["flatpak"])), x => x.Key == "found:apt:gitk");
    }

    [Fact]
    public void A_dismissed_catalog_entry_does_not_come_back_through_discovery()
    {
        var r = RecommendationEngine.Generate(Ctx([Related("lazygit")], dismissed: ["lazygit"]));
        Assert.DoesNotContain(r, x => x.Package?.PackageId == "lazygit");
    }

    [Fact]
    public void Online_results_need_a_clear_interest_in_their_niche()
    {
        var none = RecommendationEngine.Generate(Ctx([Online("org.a.B", "music")], installed: ["git"]));
        Assert.DoesNotContain(none, x => x.Key.StartsWith("found:flatpak", StringComparison.Ordinal));

        var some = RecommendationEngine.Generate(Ctx([Online("org.a.B", "gaming")], installed: ["steam", "lutris", "heroic"]));
        var rec = some.Single(x => x.Key == "found:flatpak:org.a.b");
        Assert.StartsWith("Popular on Flathub for gaming", rec.Because, StringComparison.Ordinal);
        Assert.Equal("flathub", rec.Package!.Source);
    }

    [Fact]
    public void One_niche_cannot_flood_the_list_and_the_total_is_capped()
    {
        var many = Enumerable.Range(0, 10).Select(i => Related("tool" + i)).ToList();
        Assert.Equal(2, RecommendationEngine.Generate(Ctx(many)).Count(x => x.Key.StartsWith("found:", StringComparison.Ordinal)));

        var spread = Niches8
            .SelectMany(n => new[] { Related($"a-{n}", n), Related($"b-{n}", n) }).ToList();
        var keen = new InterestProfile { Weights = Niches8.ToDictionary(n => n, _ => 5.0) };
        Assert.Equal(6, RecommendationEngine.Generate(Ctx(spread, profile: keen)).Count(x => x.Key.StartsWith("found:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_related_package_in_an_area_the_user_is_not_into_is_left_out()
    {
        var r = RecommendationEngine.Generate(Ctx([Related("somegame", "gaming")], installed: ["git", "cmake", "clang"]));
        Assert.DoesNotContain(r, x => x.Key == "found:apt:somegame");
        Assert.DoesNotContain(RecommendationEngine.Generate(Ctx([new("apt", "mystery", "mystery", "s", null, DiscoveryOrigin.Related, ["git"])])), x => x.Key == "found:apt:mystery");
    }

    [Fact]
    public void More_installed_packages_pointing_at_a_candidate_rank_it_higher()
    {
        var found = new[] { Related("weak", "development", "git"), Related("strong", "gaming", "git", "steam", "lutris") };
        var order = RecommendationEngine.Generate(Ctx(found)).Where(x => x.Key.StartsWith("found:", StringComparison.Ordinal)).Select(x => x.Title).ToList();
        Assert.Equal(["strong", "weak"], order);
    }
}

public class SignalLearningTests
{
    private static InstalledApplication App(string name) => new() { Id = "apt:" + name, Name = name, ProviderId = "apt", PackageId = name };
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Launcher_categories_classify_programs_the_catalog_does_not_know()
    {
        var signals = new SystemSignals { Launchers = [new("Some Paint Tool", ["Graphics"]), new("Other Paint Tool", ["Graphics", "2DGraphics"]), new("Third Paint Tool", ["Graphics"])] };
        var without = InterestModel.Compute(new InterestProfile(), [App("bash")]);
        var with = InterestModel.Compute(new InterestProfile(), [App("bash")], signals);
        Assert.DoesNotContain(without, i => i.Niche == "creative");
        var creative = Assert.Single(with, i => i.Niche == "creative");
        Assert.Equal(1.5, creative.Score);
        Assert.Contains("Some Paint Tool", creative.Apps);
    }

    [Fact]
    public void Launcher_weight_is_capped_so_it_cannot_double_count_a_known_program()
    {
        var launchers = Enumerable.Range(0, 30).Select(i => new LauncherEntry("Painter " + i, ["Graphics"])).ToList();
        var creative = InterestModel.Compute(new InterestProfile(), [App("gimp")], new SystemSignals { Launchers = launchers }).Single(i => i.Niche == "creative");
        Assert.Equal(3.0, creative.Score);   // gimp counts 1, launchers at most 2
    }

    [Fact]
    public void The_first_look_learns_from_recent_install_history_once()
    {
        var installed = new[] { App("steam"), App("lutris"), App("htop") };
        var signals = new SystemSignals { InstalledAt = new Dictionary<string, DateTimeOffset> { ["steam"] = Now.AddDays(-10), ["lutris"] = Now.AddDays(-400), ["htop"] = Now.AddDays(-5) } };

        var first = InterestModel.Learn(new InterestProfile(), installed, Now, signals);
        Assert.True(first.HistorySeeded);
        Assert.Equal(2.0, first.Weights["gaming"]);          // steam is recent, lutris is too old
        Assert.True(first.Weights.ContainsKey("terminal"));

        var again = InterestModel.Learn(first, installed, Now, signals);
        Assert.Equal(first.Weights["gaming"], again.Weights["gaming"]);   // not counted twice
    }

    [Fact]
    public void Without_history_the_first_look_still_only_records_a_baseline()
    {
        var p = InterestModel.Learn(new InterestProfile(), [App("steam")], Now, new SystemSignals());
        Assert.Empty(p.Weights);
        Assert.False(p.HistorySeeded);
    }

    [Fact]
    public void A_program_new_this_look_is_not_counted_twice_by_the_history()
    {
        var before = InterestModel.Learn(new InterestProfile(), [App("htop")], Now.AddDays(-1));
        var signals = new SystemSignals { InstalledAt = new Dictionary<string, DateTimeOffset> { ["steam"] = Now.AddDays(-1) } };
        var after = InterestModel.Learn(before, [App("htop"), App("steam")], Now, signals);
        Assert.Equal(2.0, after.Weights["gaming"]);
    }
}

public class DiscoveryServiceTests
{
    private sealed class Settings : ISettingsStore
    {
        public AppSettings Value { get; set; } = new();
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class Interests : IInterestStore
    {
        public InterestProfile Value { get; set; } = new();
        public Task<InterestProfile> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(InterestProfile profile, CancellationToken ct) { Value = profile; return Task.CompletedTask; }
    }

    private sealed class Source(bool network, Func<DiscoveryRequest, IReadOnlyList<DiscoveredPackage>> work) : IDiscoverySource
    {
        public int Calls { get; private set; }
        public DiscoveryRequest? Last { get; private set; }
        public bool RequiresNetwork => network;

        public Task<IReadOnlyList<DiscoveredPackage>> DiscoverAsync(DiscoveryRequest request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(work(request));
        }
    }

    private sealed class Signals(SystemSignals value) : ISystemSignalSource
    {
        public Task<SystemSignals> ReadAsync(CancellationToken ct) => Task.FromResult(value);
    }

    private sealed class NoLock : IEngineLock
    {
        public IDisposable? TryAcquire() => null;
    }

    private static RecommendationService Service(Settings settings, Interests interests, IEnumerable<IDiscoverySource>? sources = null, ISystemSignalSource? signals = null) =>
        new(new PackageManagerService(new ProviderRegistry([]), settings, new NoLock()), settings, interests, null, sources, signals);

    private static InstalledApplication App(string name) => new() { Id = "apt:" + name, Name = name, ProviderId = "apt", PackageId = name };

    [Fact]
    public async Task Online_sources_run_only_after_the_user_opts_in_and_see_only_interest_names()
    {
        var local = new Source(false, _ => []);
        var online = new Source(true, _ => []);
        var settings = new Settings();
        var svc = Service(settings, new Interests(), [local, online]);
        var apps = new[] { App("steam"), App("lutris"), App("heroic") };

        await svc.GetAsync(apps, TestContext.Current.CancellationToken);
        Assert.Equal(1, local.Calls);
        Assert.Equal(0, online.Calls);

        await svc.SetDiscoveryAsync(null, true, TestContext.Current.CancellationToken);
        await svc.GetAsync(apps, TestContext.Current.CancellationToken);
        Assert.Equal(1, online.Calls);
        Assert.Contains("gaming", online.Last!.Niches);
    }

    [Fact]
    public async Task Turning_the_repository_lookup_off_stops_the_local_source()
    {
        var local = new Source(false, _ => []);
        var svc = Service(new Settings { Value = new AppSettings { DiscoverFromRepositories = false } }, new Interests(), [local]);
        await svc.GetAsync([App("git")], TestContext.Current.CancellationToken);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task A_failing_source_never_breaks_the_page()
    {
        var bad = new Source(false, _ => throw new InvalidOperationException("boom"));
        var svc = Service(new Settings(), new Interests(), [bad]);
        var report = await svc.GetAsync([App("git")], TestContext.Current.CancellationToken);
        Assert.NotNull(report);
        Assert.True(report.Discovery!.RepositoriesAvailable);
        Assert.False(report.Discovery.OnlineAvailable);
    }

    [Fact]
    public async Task Results_are_reused_until_the_installed_set_changes()
    {
        var local = new Source(false, _ => []);
        var svc = Service(new Settings(), new Interests(), [local]);
        await svc.GetAsync([App("git")], TestContext.Current.CancellationToken);
        await svc.GetAsync([App("git")], TestContext.Current.CancellationToken);
        Assert.Equal(1, local.Calls);
        await svc.GetAsync([App("git"), App("steam")], TestContext.Current.CancellationToken);
        Assert.Equal(2, local.Calls);
    }

    [Fact]
    public async Task Forgetting_does_not_bring_the_install_history_back()
    {
        var interests = new Interests();
        var signals = new SystemSignals { InstalledAt = new Dictionary<string, DateTimeOffset> { ["steam"] = DateTimeOffset.UtcNow.AddDays(-2) } };
        var svc = Service(new Settings(), interests, null, new Signals(signals));
        var apps = new[] { App("steam"), App("htop") };

        await svc.LearnAsync(apps, TestContext.Current.CancellationToken);
        Assert.True(interests.Value.Weights.ContainsKey("gaming"));

        await svc.ForgetAsync(TestContext.Current.CancellationToken);
        await svc.LearnAsync(apps, TestContext.Current.CancellationToken);
        Assert.Empty(interests.Value.Weights);
    }
}
