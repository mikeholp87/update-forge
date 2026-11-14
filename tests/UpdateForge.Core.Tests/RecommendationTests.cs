using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Recommendations;
using UpdateForge.Infrastructure.Persistence;
using Xunit;

namespace UpdateForge.Core.Tests;

public class RecommendationEngineTests
{
    private static InstalledApplication App(string name, string provider, string? id = null, bool pinned = false) =>
        new() { Id = $"{provider}:{id ?? name}", Name = name, ProviderId = provider, PackageId = id ?? name, IsPinned = pinned };

    private static RecommendationContext Ctx(IEnumerable<InstalledApplication> installed, string[]? providers = null, string[]? dismissed = null, string[]? disabled = null) => new()
    {
        Installed = [.. installed],
        InstallableProviders = (providers ?? ["apt", "flatpak", "brew", "cargo", "pipx", "npm"]).ToHashSet(),
        Dismissed = (dismissed ?? []).ToHashSet(),
        DisabledManagers = disabled ?? [],
    };

    private static Recommendation? Find(IEnumerable<Recommendation> r, string key) => r.FirstOrDefault(x => x.Key == key);

    [Fact]
    public void A_specific_trigger_suggests_its_package_with_the_reason()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam", "apt")]));
        var proton = Find(r, "gamemode");
        Assert.NotNull(proton);
        Assert.Equal("apt", proton!.Package!.ProviderId);
        Assert.Equal("gamemode", proton.Package.PackageId);
        Assert.Equal("Because you have steam", proton.Because);
        Assert.NotNull(proton.Tip);
    }

    [Fact]
    public void A_flatpak_id_matches_by_its_last_segment()
    {
        var r = RecommendationEngine.Generate(Ctx([App("Steam", "flatpak", "com.valvesoftware.Steam")]));
        Assert.NotNull(Find(r, "protonup-qt"));
    }

    [Fact]
    public void Anything_from_a_manager_can_be_the_trigger()
    {
        var r = RecommendationEngine.Generate(Ctx([App("Firefox", "flatpak", "org.mozilla.firefox")]));
        Assert.Equal("Because you have flatpak packages", Find(r, "flatseal")!.Because);
        Assert.Null(Find(r, "needrestart"));   // no apt packages installed
    }

    [Fact]
    public void Already_installed_in_any_manager_is_not_suggested_again()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam", "apt"), App("gamemode", "apt"), App("mangohud", "brew")]));
        Assert.Null(Find(r, "gamemode"));
        Assert.Null(Find(r, "mangohud"));          // installed with a different manager
        Assert.NotNull(Find(r, "protonup-qt"));
    }

    [Fact]
    public void A_flatpak_already_installed_is_recognised_by_its_title()
    {
        var r = RecommendationEngine.Generate(Ctx([App("Flatseal", "flatpak", "com.github.tchx84.Flatseal")]));
        Assert.Null(Find(r, "flatseal"));
    }

    [Fact]
    public void The_first_usable_manager_is_chosen_in_preference_order()
    {
        var installed = new[] { App("bash", "apt") };
        Assert.Equal("apt", Find(RecommendationEngine.Generate(Ctx(installed)), "ripgrep")!.Package!.ProviderId);
        Assert.Equal("brew", Find(RecommendationEngine.Generate(Ctx(installed, providers: ["brew", "cargo"])), "ripgrep")!.Package!.ProviderId);
        Assert.Equal("cargo", Find(RecommendationEngine.Generate(Ctx(installed, providers: ["cargo"])), "ripgrep")!.Package!.ProviderId);
    }

    [Fact]
    public void Nothing_is_suggested_when_no_usable_manager_can_install_it()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam", "apt")], providers: []));
        Assert.DoesNotContain(r, x => x.Kind == RecommendationKind.Package);
    }

    [Fact]
    public void Flatpak_suggestions_carry_their_remote()
    {
        var p = Find(RecommendationEngine.Generate(Ctx([App("steam", "apt")])), "protonup-qt")!.Package!;
        Assert.Equal(("flatpak", "net.davidotek.pupgui2", "flathub"), (p.ProviderId, p.PackageId, p.Source));
    }

    [Fact]
    public void Dismissed_suggestions_stay_hidden()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam", "apt")], dismissed: ["gamemode"]));
        Assert.Null(Find(r, "gamemode"));
        Assert.NotNull(Find(r, "mangohud"));
    }

    [Fact]
    public void Specific_suggestions_rank_above_general_ones_and_the_list_is_capped()
    {
        var r = RecommendationEngine.Generate(Ctx([App("bash", "apt"), App("steam", "apt"), App("git", "apt"), App("htop", "apt"), App("python3", "apt")]), maxPackages: 5);
        var packages = r.Where(x => x.Kind == RecommendationKind.Package).ToList();
        Assert.Equal(5, packages.Count);
        Assert.Equal(packages.OrderByDescending(p => p.Priority).Select(p => p.Key), packages.Select(p => p.Key));
        Assert.DoesNotContain(packages, p => p.Key == "eza");   // a broad, low-priority one falls off the end
    }

    [Fact]
    public void Broad_suggestions_do_not_claim_a_reason_they_do_not_have()
    {
        var r = RecommendationEngine.Generate(Ctx([App("bash", "apt")]));
        Assert.Null(Find(r, "ripgrep")!.Because);
    }

    [Fact]
    public void The_same_program_under_two_managers_is_flagged_but_package_manager_internals_are_not()
    {
        var r = RecommendationEngine.Generate(Ctx([App("firefox", "apt"), App("Firefox", "flatpak", "org.mozilla.firefox"), App("snapd", "apt"), App("snapd", "snap")]));
        var tip = Find(r, "tip:duplicates")!;
        Assert.Equal(RecommendationKind.Tip, tip.Kind);
        Assert.Contains("firefox (apt and flatpak)", tip.Tip!);
        Assert.DoesNotContain("snapd", tip.Tip!);
        Assert.Contains("One program", tip.Title);
    }

    [Fact]
    public void The_same_name_under_one_manager_is_not_a_duplicate()
    {
        var r = RecommendationEngine.Generate(Ctx([App("vim", "apt", "vim"), App("vim", "apt", "vim-gtk3")]));
        Assert.Null(Find(r, "tip:duplicates"));
    }

    [Fact]
    public void Held_packages_and_switched_off_managers_become_tips()
    {
        var r = RecommendationEngine.Generate(Ctx([App("libfoo", "apt", pinned: true)], disabled: ["Flatpak"]));
        Assert.Contains("libfoo", Find(r, "tip:held")!.Tip!);
        Assert.Equal("Flatpak is installed but turned off", Find(r, "tip:disabled:flatpak")!.Title);
        Assert.Null(Find(RecommendationEngine.Generate(Ctx([App("libfoo", "apt", pinned: true)], dismissed: ["tip:held"])), "tip:held"));
    }

    [Fact]
    public void The_same_inventory_always_gives_the_same_list()
    {
        var installed = new[] { App("bash", "apt"), App("steam", "apt"), App("git", "apt") };
        Assert.Equal(RecommendationEngine.Generate(Ctx(installed)).Select(r => r.Key), RecommendationEngine.Generate(Ctx(installed)).Select(r => r.Key));
    }

    [Fact]
    public void Every_catalog_entry_is_well_formed()
    {
        var rules = RecommendationCatalog.Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.Key).Distinct().Count());
        foreach (var rule in rules)
        {
            Assert.True(rule.When.Length > 0 || rule.Niches is { Length: > 0 }, rule.Key + " needs a trigger or a niche");
            Assert.NotEmpty(rule.Candidates);
            Assert.All(rule.When, w => Assert.Equal(w.ToLowerInvariant(), w));      // matching is on lower-case names
            Assert.False(string.IsNullOrWhiteSpace(rule.Why));
            Assert.True(rule.Why.Length < 140, rule.Key + " reason is too long to read at a glance");
        }
    }
}

public class RecommendationServiceTests
{
    private sealed class Settings : ISettingsStore
    {
        public AppSettings Value { get; set; } = new() { EnabledProviders = ["test"] };
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class InterestStoreFake : IInterestStore
    {
        public InterestProfile Value { get; set; } = new();
        public Task<InterestProfile> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(InterestProfile profile, CancellationToken ct) { Value = profile; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Dismiss_and_restore_round_trip_and_never_duplicate()
    {
        var store = new Settings();
        var svc = new RecommendationService(null!, store, new InterestStoreFake());
        var rec = new Recommendation("gamemode", RecommendationKind.Package, "GameMode", "Gaming", "why", null, null, null, 60, ["gaming"]);
        await svc.DismissAsync(rec, TestContext.Current.CancellationToken);
        await svc.DismissAsync(rec, TestContext.Current.CancellationToken);
        Assert.Equal(["gamemode"], store.Value.DismissedRecommendations);
        await svc.RestoreDismissedAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.Value.DismissedRecommendations);
    }

    [Fact]
    public void Settings_validation_trims_junk_and_caps_dismissals()
    {
        var s = new AppSettings { DismissedRecommendations = ["a", "a", " ", new string('x', 200), .. Enumerable.Range(0, 300).Select(i => "k" + i)] }.Validated();
        Assert.Equal(200, s.DismissedRecommendations.Count);
        Assert.Equal(1, s.DismissedRecommendations.Count(k => k == "a"));
        Assert.DoesNotContain(s.DismissedRecommendations, k => k.Length > 80);
    }
}

public class InterestLearningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static InstalledApplication App(string name, string provider = "apt", string? id = null) =>
        new() { Id = $"{provider}:{id ?? name}", Name = name, ProviderId = provider, PackageId = id ?? name };

    private static RecommendationContext Ctx(IEnumerable<InstalledApplication> installed, InterestProfile? profile = null, string[]? dismissed = null) => new()
    {
        Installed = [.. installed],
        InstallableProviders = new HashSet<string> { "apt", "flatpak", "brew", "cargo", "pipx", "npm" },
        Dismissed = (dismissed ?? []).ToHashSet(),
        Profile = profile ?? new InterestProfile(),
    };

    private static readonly string[] GamingNames = ["steam", "lutris", "heroic", "bottles", "retroarch", "wine", "winetricks"];

    private static double Score(IEnumerable<Interest> interests, string niche) => interests.FirstOrDefault(i => i.Niche == niche)?.Score ?? 0;

    [Fact]
    public void Installed_programs_are_classified_into_interest_areas_with_the_names_that_show_it()
    {
        var interests = InterestModel.Compute(new InterestProfile(), [App("steam"), App("lutris"), App("vlc"), App("bash")]);
        var gaming = interests.Single(i => i.Niche == "gaming");
        Assert.Equal(2, gaming.Score);
        Assert.Equal(["steam", "lutris"], gaming.Apps);
        Assert.Equal("Gaming", gaming.Name);
        Assert.DoesNotContain(interests, i => i.Apps.Contains("bash"));       // ubiquitous programs say nothing
    }

    [Fact]
    public void A_flatpak_id_is_classified_by_its_last_segment()
    {
        Assert.True(Score(InterestModel.Compute(new InterestProfile(), [App("Steam", "flatpak", "com.valvesoftware.Steam"), App("Heroic", "flatpak", "com.heroicgameslauncher.hgl")]), "gaming") >= 2);
    }

    [Fact]
    public void Developer_package_managers_count_toward_development_even_for_unknown_packages()
    {
        var dev = InterestModel.Compute(new InterestProfile(), [App("9router", "npm"), App("some-tool", "cargo"), App("another", "pipx"), App("x", "npm"), App("y", "npm")]).Single(i => i.Niche == "development");
        Assert.Equal(2, dev.Score);          // four programs, half a point each
        Assert.Empty(dev.Apps);
    }

    [Fact]
    public void The_first_look_is_a_baseline_and_teaches_nothing()
    {
        var p = InterestModel.Learn(new InterestProfile(), [App("steam"), App("lutris"), App("vlc")], T0);
        Assert.Empty(p.Weights);
        Assert.Equal(3, p.SeenApps.Count);
        Assert.Equal(T0, p.UpdatedAt);
    }

    [Fact]
    public void Programs_that_appear_later_strengthen_their_niche()
    {
        var baseline = InterestModel.Learn(new InterestProfile(), [App("steam")], T0);
        var p = InterestModel.Learn(baseline, [App("steam"), App("heroic", "flatpak", "com.heroicgameslauncher.hgl"), App("some-unknown-tool")], T0.AddDays(1));
        Assert.Equal(2, p.Weights["gaming"], 2);
        Assert.DoesNotContain("development", p.Weights.Keys);                 // an unclassified program cannot teach anything
        Assert.Equal(baseline.SeenApps.Count + 2, p.SeenApps.Count);
    }

    [Fact]
    public void One_burst_of_installs_cannot_swamp_the_profile()
    {
        var baseline = InterestModel.Learn(new InterestProfile(), [App("bash")], T0);
        var many = GamingNames.Select(n => App(n));
        var p = InterestModel.Learn(baseline, [App("bash"), .. many], T0.AddDays(1));
        Assert.Equal(6, p.Weights["gaming"], 2);
    }

    [Fact]
    public void Learning_the_same_inventory_again_changes_nothing()
    {
        var installed = new[] { App("steam"), App("vlc") };
        var first = InterestModel.Learn(new InterestProfile(), installed, T0);
        var second = InterestModel.Learn(first, installed, T0.AddHours(1));
        Assert.Equal(first.Weights, second.Weights);
    }

    [Fact]
    public void What_was_learned_fades_with_a_ninety_day_half_life()
    {
        var p = new InterestProfile { Weights = new Dictionary<string, double> { ["gaming"] = 8 }, SeenApps = ["x"], UpdatedAt = T0 };
        Assert.Equal(4, InterestModel.Learn(p, [App("x")], T0.AddDays(90)).Weights["gaming"], 2);
        Assert.Equal(2, InterestModel.Learn(p, [App("x")], T0.AddDays(180)).Weights["gaming"], 2);
    }

    [Fact]
    public void Installing_a_suggestion_teaches_and_turning_one_down_untaches()
    {
        var p = InterestModel.RecordInstalled(new InterestProfile(), ["gaming"], T0);
        Assert.Equal(3, p.Weights["gaming"]);
        p = InterestModel.RecordDismissed(p, ["gaming", "not-a-niche"], T0);
        Assert.Equal(1, p.Weights["gaming"]);
        Assert.DoesNotContain("not-a-niche", p.Weights.Keys);
    }

    [Fact]
    public void Weights_stay_within_bounds()
    {
        var p = new InterestProfile();
        for (var i = 0; i < 40; i++) p = InterestModel.RecordInstalled(p, ["gaming"], T0);
        Assert.Equal(20, p.Weights["gaming"]);
        for (var i = 0; i < 40; i++) p = InterestModel.RecordDismissed(p, ["media"], T0);
        Assert.Equal(-5, p.Weights["media"]);
    }

    [Fact]
    public void A_clear_interest_brings_suggestions_from_that_niche_with_the_reason()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam"), App("lutris")]));
        var heroic = r.Single(x => x.Key == "heroic");
        Assert.Equal("You're into gaming: you have steam, lutris", heroic.Because);
        Assert.Equal(["gaming"], heroic.Niches);
        Assert.Equal("flatpak", heroic.Package!.ProviderId);
    }

    [Fact]
    public void A_single_program_is_not_yet_an_interest()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam")]));
        Assert.DoesNotContain(r, x => x.Key == "heroic");          // one game launcher is below the threshold
        Assert.Contains(r, x => x.Key == "gamemode");              // but the specific steam rule still fires
    }

    [Fact]
    public void Learning_alone_can_create_an_interest_and_says_so()
    {
        var profile = new InterestProfile { Weights = new Dictionary<string, double> { ["music"] = 4 }, SeenApps = ["x"], UpdatedAt = T0 };
        var r = RecommendationEngine.Generate(Ctx([App("firefox")], profile));
        var any = r.First(x => x.Niches?.Contains("music") == true);
        Assert.Equal("You're into music making, learned from what you installed", any.Because);
    }

    [Fact]
    public void Turning_suggestions_down_makes_the_niche_need_more_evidence()
    {
        var installed = new[] { App("steam"), App("lutris") };
        var profile = InterestModel.RecordDismissed(InterestModel.RecordDismissed(new InterestProfile(), ["gaming"], T0), ["gaming"], T0);   // -4
        Assert.DoesNotContain(RecommendationEngine.Generate(Ctx(installed, profile)), x => x.Key == "heroic");
    }

    [Fact]
    public void One_niche_cannot_flood_the_list_and_installed_programs_are_never_suggested()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam"), App("lutris"), App("retroarch"), App("heroic", "flatpak", "com.heroicgameslauncher.hgl")]));
        var gamingOnly = r.Where(x => x.Because?.StartsWith("You're into gaming", StringComparison.Ordinal) == true).ToList();
        Assert.True(gamingOnly.Count <= 2);
        Assert.DoesNotContain(r, x => x.Key is "lutris" or "retroarch" or "heroic");
    }

    [Fact]
    public void Dismissed_niche_suggestions_stay_hidden()
    {
        var r = RecommendationEngine.Generate(Ctx([App("steam"), App("lutris")], dismissed: ["heroic", "bottles", "prism-launcher", "retroarch", "dolphin-emu", "goverlay"]));
        Assert.DoesNotContain(r, x => x.Because?.StartsWith("You're into gaming", StringComparison.Ordinal) == true);
    }
}

public class InterestServiceTests
{
    private sealed class Settings : ISettingsStore
    {
        public AppSettings Value { get; set; } = new();
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class Store : IInterestStore
    {
        public InterestProfile Value { get; set; } = new();
        public int Saves { get; private set; }
        public Task<InterestProfile> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(InterestProfile profile, CancellationToken ct) { Value = profile; Saves++; return Task.CompletedTask; }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static InstalledApplication App(string name) => new() { Id = "apt:" + name, Name = name, ProviderId = "apt", PackageId = name };
    private static Recommendation Rec(params string[] niches) => new("heroic", RecommendationKind.Package, "Heroic", "Gaming", "why", null, null, null, 60, niches);

    private static (RecommendationService svc, Settings settings, Store store, Clock clock) Make()
    {
        var s = new Settings(); var i = new Store(); var c = new Clock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        return (new RecommendationService(null!, s, i, c), s, i, c);
    }

    [Fact]
    public async Task Each_scan_teaches_what_is_new()
    {
        var (svc, _, store, clock) = Make();
        await svc.LearnAsync([App("bash")], TestContext.Current.CancellationToken);          // baseline
        clock.Now = clock.Now.AddDays(1);
        await svc.LearnAsync([App("bash"), App("steam")], TestContext.Current.CancellationToken);
        Assert.Equal(2, store.Value.Weights["gaming"], 2);
    }

    [Fact]
    public async Task Switching_learning_off_stops_all_learning()
    {
        var (svc, _, store, _) = Make();
        await svc.SetLearningAsync(false, TestContext.Current.CancellationToken);
        await svc.LearnAsync([App("steam")], TestContext.Current.CancellationToken);
        await svc.RecordInstalledAsync(Rec("gaming"), TestContext.Current.CancellationToken);
        await svc.DismissAsync(Rec("gaming"), TestContext.Current.CancellationToken);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Accepting_and_declining_suggestions_are_remembered()
    {
        var (svc, settings, store, _) = Make();
        await svc.RecordInstalledAsync(Rec("gaming"), TestContext.Current.CancellationToken);
        Assert.Equal(3, store.Value.Weights["gaming"]);
        await svc.DismissAsync(Rec("gaming"), TestContext.Current.CancellationToken);
        Assert.Equal(1, store.Value.Weights["gaming"], 2);
        Assert.Equal(["heroic"], settings.Value.DismissedRecommendations);
    }

    [Fact]
    public async Task Forgetting_wipes_everything_and_the_next_look_is_a_fresh_baseline()
    {
        var (svc, _, store, clock) = Make();
        await svc.LearnAsync([App("bash")], TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddDays(1);
        await svc.LearnAsync([App("bash"), App("steam")], TestContext.Current.CancellationToken);
        await svc.ForgetAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.Value.Weights);
        Assert.Empty(store.Value.SeenApps);
        await svc.LearnAsync([App("bash"), App("steam")], TestContext.Current.CancellationToken);
        Assert.Empty(store.Value.Weights);                       // what is installed today is not "new"
    }

    [Fact]
    public async Task An_empty_inventory_never_overwrites_what_was_learned()
    {
        var (svc, _, store, _) = Make();
        await svc.RecordInstalledAsync(Rec("gaming"), TestContext.Current.CancellationToken);
        var saves = store.Saves;
        await svc.LearnAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(saves, store.Saves);
        Assert.Equal(3, store.Value.Weights["gaming"]);
    }
}

public class InterestStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"uf-interests-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*")) File.Delete(f);
        GC.SuppressFinalize(this);
    }

    private SqliteInterestStore Store()
    {
        var db = new Database(_path);
        db.Migrate();
        return new SqliteInterestStore(db);
    }

    [Fact]
    public async Task A_profile_survives_a_restart()
    {
        var saved = new InterestProfile { Weights = new Dictionary<string, double> { ["gaming"] = 4.5 }, SeenApps = ["steam", "vlc"], UpdatedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        await Store().SaveAsync(saved, TestContext.Current.CancellationToken);
        var loaded = await Store().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4.5, loaded.Weights["gaming"]);
        Assert.Equal(["steam", "vlc"], loaded.SeenApps);
        Assert.Equal(saved.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Nothing_saved_or_unreadable_data_means_start_fresh()
    {
        Assert.Empty((await Store().LoadAsync(TestContext.Current.CancellationToken)).Weights);
        var db = new Database(_path);
        using (var c = db.Open()) Database.Exec(c, null, "INSERT OR REPLACE INTO Settings (Key,Value) VALUES ('interests','{not json');");
        Assert.Empty((await Store().LoadAsync(TestContext.Current.CancellationToken)).SeenApps);
    }
}
