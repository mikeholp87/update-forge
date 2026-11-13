using Microsoft.Extensions.Logging.Abstractions;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Providers.Chocolatey;
using UpdateForge.Providers.Scoop;
using UpdateForge.Providers.WinGet;
using Xunit;

namespace UpdateForge.Core.Tests;

public class PackageSearchTests
{
    private static ProcessResult Ok(string output) => new(0, output, "", false);

    [Fact]
    public async Task Search_combines_enabled_managers_preserving_source_and_manager_identities()
    {
        var winget = new SearchProvider("winget", [new("winget", "Git.Git", "Git", "2.0", "winget"),
            new("winget", "Git.Git", "Git", "2.0", "winget"), new("winget", "Git.Git", "Git", "2.0", "private")]);
        var scoop = new SearchProvider("scoop", [new("scoop", "git", "git", "2.0", "main")]);
        var disabled = new SearchProvider("disabled", []);
        var service = new PackageSearchService(new ProviderRegistry([winget, scoop, disabled, new FakeProvider("registry")]),
            new SearchSettings("WINGET", "scoop", "registry"));

        var result = await service.SearchAsync("  git  ", TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Packages.Count);
        Assert.Equal(2, result.Providers.Count);
        Assert.All(result.Providers, p => Assert.Null(p.Error));
        Assert.Equal("git", winget.LastQuery);
        Assert.Equal("git", scoop.LastQuery);
        Assert.Null(disabled.LastQuery);
        Assert.False(disabled.AvailabilityChecked);
    }

    [Fact]
    public async Task Search_reloads_saved_manager_selection()
    {
        var provider = new SearchProvider("scoop", []);
        var settings = new SearchSettings("scoop");
        var service = new PackageSearchService(new ProviderRegistry([provider]), settings);
        Assert.Single((await service.SearchAsync("git", TestContext.Current.CancellationToken)).Providers);
        settings.Value = new AppSettings { EnabledProviders = [] };
        Assert.Empty((await service.SearchAsync("git", TestContext.Current.CancellationToken)).Providers);
    }

    [Fact]
    public async Task Failed_and_unavailable_managers_do_not_hide_successful_results()
    {
        var ok = new SearchProvider("ok", [new("ok", "git", "Git", "2.0", "main")]);
        var broken = new SearchProvider("broken", []) { Search = (_, _) => throw new TimeoutException("Search timed out.") };
        var missing = new SearchProvider("missing", []) { Availability = ProviderAvailability.NotInstalled("Install the manager first.") };
        var service = new PackageSearchService(new ProviderRegistry([ok, broken, missing]), new SearchSettings("ok", "broken", "missing"));
        var result = await service.SearchAsync("git", TestContext.Current.CancellationToken);
        Assert.Single(result.Packages);
        Assert.Equal("Search timed out.", result.Providers.Single(p => p.ProviderId == "broken").Error);
        Assert.Equal("Install the manager first.", result.Providers.Single(p => p.ProviderId == "missing").Error);
        Assert.Null(missing.LastQuery);
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_provider_and_caller()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new SearchProvider("scoop", []) { Search = (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<PackageSearchResult>>([]); } };
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("git", cancel.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--source")]
    [InlineData("git\ninstall")]
    [InlineData("git\n")]
    public async Task Invalid_queries_never_start_managers(string query)
    {
        var runner = new ScriptedRunner(_ => Ok(""));
        await Assert.ThrowsAsync<ArgumentException>(() => new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, "winget")
            .SearchPackagesAsync(query, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "scoop", "powershell")
            .SearchPackagesAsync(query, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "choco")
            .SearchPackagesAsync(query, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Winget_search_parses_new_packages_and_custom_sources_with_localized_headers()
    {
        static string Row(string name, string id, string version, string match, string source) => $"{name,-24}{id,-30}{version,-12}{match,-20}{source}";
        var output = string.Join('\n', "Downloading source...", Row("Nom", "Identifiant", "Version", "Correspondance", "Source"), new string('-', 100),
            Row("Notepad++", "Notepad++.Notepad++", "8.8.0", "Tag: editor", "winget"),
            Row("Internal Editor", "Company.Editor", "1.0", "", "private"),
            Row("Truncated", "Company.Long...", "1.0", "", "private"));
        var packages = WinGetProvider.ParsePackageSearch(output);
        Assert.Equal(2, packages.Count);
        Assert.Equal("Notepad++.Notepad++", packages[0].PackageId);
        Assert.Equal("Internal Editor", packages[1].Name);
        Assert.Equal("private", packages[1].Source);
    }

    [Fact]
    public void Winget_search_preserves_ids_with_wide_unicode_names_and_headers()
    {
        var header = "名称" + new string(' ', 20) + $"{"Id",-30}{"Version",-12}{"Match",-20}Source";
        var row = "微信" + new string(' ', 20) + $"{"Vendor.WeChat",-30}{"4.0",-12}{"",-20}msstore";
        var package = Assert.Single(WinGetProvider.ParsePackageSearch(string.Join('\n', header, new string('-', 100), row)));
        Assert.Equal("微信", package.Name);
        Assert.Equal("Vendor.WeChat", package.PackageId);
        Assert.Equal("4.0", package.Version);
        Assert.Equal("msstore", package.Source);
    }

    [Fact]
    public void Winget_search_handles_omitted_empty_columns()
    {
        static string Row(string name, string id, string version, string last) => $"{name,-24}{id,-30}{version,-12}{last}";
        var sourceTable = string.Join('\n', Row("Name", "Id", "Version", "Source"), new string('-', 80),
            Row("Git", "Git.Git", "2.45.0", "private"));
        Assert.Equal("private", Assert.Single(WinGetProvider.ParsePackageSearch(sourceTable)).Source);
        var matchTable = string.Join('\n', Row("Name", "Id", "Version", "Match"), new string('-', 80),
            Row("Git", "Git.Git", "2.45.0", "Moniker: git"));
        Assert.Null(Assert.Single(WinGetProvider.ParsePackageSearch(matchTable)).Source);
        var versionTable = string.Join('\n', Row("Name", "Id", "Version", "").TrimEnd(), new string('-', 65),
            Row("Git", "Git.Git", "2.45.0", "").TrimEnd());
        Assert.Null(Assert.Single(WinGetProvider.ParsePackageSearch(versionTable)).Source);
    }

    [Fact]
    public async Task Winget_search_uses_native_catalogs_and_handles_no_matches()
    {
        var runner = new ScriptedRunner(_ => new(unchecked((int)0x8A150014), "No package found.", "", false));
        var provider = new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, "winget");
        Assert.Empty(await provider.SearchPackagesAsync("visual studio", TestContext.Current.CancellationToken));
        var args = runner.Calls.Single().Arguments;
        Assert.Equal("search", args[0]);
        Assert.Contains("visual studio", args);
        Assert.DoesNotContain("--source", args);
        Assert.DoesNotContain("--count", args);
        Assert.Contains("--disable-interactivity", args);
    }

    [Fact]
    public void Scoop_search_parses_buckets_and_excludes_unconfigured_remote_suggestions()
    {
        var packages = ScoopProvider.ParsePackageSearch("""
            Results from local buckets...
            Name       Version Source Binaries
            ----       ------- ------ --------
            git        2.45.0  main
            git        2.46.0  custom git.exe

            Results from other known buckets...
            Name       Source
            ----       ------
            gitextras  extras
            """);
        Assert.Equal(2, packages.Count);
        Assert.Equal("main", packages[0].Source);
        Assert.Equal("custom", packages[1].Source);
    }

    [Fact]
    public void Scoop_search_supports_older_bucket_output()
    {
        var package = Assert.Single(ScoopProvider.ParsePackageSearch("'main' bucket:\n    git (2.45.0)\n"));
        Assert.Equal("main", package.Source);
        Assert.Equal("2.45.0", package.Version);
    }

    [Fact]
    public async Task Scoop_search_escapes_regex_metacharacters_and_handles_no_matches()
    {
        var runner = new ScriptedRunner(s => s.Arguments.Contains("config") ? Ok("False") : new(1, "WARN No matches found.", "", false));
        var provider = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "scoop", "powershell");
        Assert.Empty(await provider.SearchPackagesAsync("notepad++", TestContext.Current.CancellationToken));
        var search = runner.Calls.Single(s => s.Arguments.Contains("search"));
        Assert.Contains("notepad\\+\\+", search.Arguments);
        Assert.Contains("-File", search.Arguments);
    }

    [Fact]
    public async Task Scoop_cached_search_passes_literal_query_without_regex_escapes()
    {
        var runner = new ScriptedRunner(s => s.Arguments.Contains("config") ? Ok("True") : Ok(""));
        var provider = new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "scoop", "powershell");
        await provider.SearchPackagesAsync("notepad++", TestContext.Current.CancellationToken);
        Assert.Contains("notepad++", runner.Calls.Single(s => s.Arguments.Contains("search")).Arguments);
    }

    [Fact]
    public async Task Chocolatey_search_queries_configured_sources_and_parses_machine_output()
    {
        var runner = new ScriptedRunner(_ => Ok("git|2.45.0\ngit.install|2.45.0\n"));
        var provider = new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "choco");
        var result = await provider.SearchPackagesAsync("git", TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        var args = runner.Calls.Single().Arguments;
        Assert.Equal("search", args[0]);
        Assert.DoesNotContain("--source", args);
        Assert.DoesNotContain("--local-only", args);
        Assert.Contains("--limit-output", args);
    }

    [Fact]
    public async Task Chocolatey_enhanced_no_results_exit_is_an_empty_result()
    {
        var runner = new ScriptedRunner(_ => new(2, "", "", false));
        Assert.Empty(await new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "choco")
            .SearchPackagesAsync("nothing", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Search_failures_are_errors_instead_of_empty_success(bool timedOut, int exitCode)
    {
        var runner = new ScriptedRunner(_ => new(exitCode, "", "Source unavailable", timedOut));
        var providers = new IPackageSearchProvider[]
        {
            new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, "winget"),
            new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, "scoop", "powershell"),
            new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, "choco"),
        };
        foreach (var provider in providers)
        {
            var error = await Record.ExceptionAsync(() => provider.SearchPackagesAsync("git", TestContext.Current.CancellationToken));
            Assert.NotNull(error);
            if (timedOut) Assert.IsType<TimeoutException>(error);
            else Assert.IsType<InvalidOperationException>(error);
        }
    }

    [Fact]
    public async Task Native_catalog_search_does_not_launch_extra_version_processes()
    {
        var executable = Path.GetTempFileName();
        try
        {
            var runner = new ScriptedRunner(_ => Ok(""));
            var providers = new IUpdateProvider[]
            {
                new WinGetProvider(runner, NullLogger<WinGetProvider>.Instance, executable),
                new ScoopProvider(runner, NullLogger<ScoopProvider>.Instance, executable, executable),
                new ChocolateyProvider(runner, NullLogger<ChocolateyProvider>.Instance, executable),
            };
            var service = new PackageSearchService(new ProviderRegistry(providers), new SearchSettings("winget", "scoop", "chocolatey"));
            await service.SearchAsync("git", TestContext.Current.CancellationToken);
            Assert.Equal(3, runner.Calls.Count);
            Assert.All(runner.Calls, call => Assert.Contains("search", call.Arguments));
            await service.SearchAsync("git", TestContext.Current.CancellationToken);
            Assert.Equal(3, runner.Calls.Count);
        }
        finally { File.Delete(executable); }
    }

    [Fact]
    public async Task Fast_manager_is_visible_before_slow_manager_finishes()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new SearchProvider("fast", [new("fast", "git", "Git", "2.0", "main")]);
        var slow = new SearchProvider("slow", []) { Search = (_, ct) => pending.Task.WaitAsync(ct) };
        var service = new PackageSearchService(new ProviderRegistry([fast, slow]), new SearchSettings("fast", "slow"));
        await using var results = service.SearchProvidersAsync("git", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("fast", results.Current.ProviderId);
        Assert.Single(results.Current.Packages);
        var next = results.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        pending.SetResult([]);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("slow", results.Current.ProviderId);
    }

    [Fact]
    public async Task Repeat_search_uses_cache_without_starting_manager_or_version_probe()
    {
        var provider = new SearchProvider("scoop", [new("scoop", "git", "Git", "2.0", "main")]);
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        var first = await service.SearchAsync("git", TestContext.Current.CancellationToken);
        var repeated = await service.SearchAsync(" GIT ", TestContext.Current.CancellationToken);
        Assert.False(first.Providers.Single().IsCached);
        Assert.True(repeated.Providers.Single().IsCached);
        Assert.Equal(first.Packages, repeated.Packages);
        Assert.Equal(1, provider.SearchCalls);
        Assert.Equal(1, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task New_query_reuses_recent_health_check_but_still_searches_catalog()
    {
        var provider = new SearchProvider("scoop", []);
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        await service.SearchAsync("node", TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.SearchCalls);
        Assert.Equal(1, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task Refresh_bypasses_cached_results_and_health_checks()
    {
        var provider = new SearchProvider("scoop", []);
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        var fresh = await service.SearchAsync("git", TestContext.Current.CancellationToken, refresh: true);
        Assert.False(fresh.Providers.Single().IsCached);
        Assert.Equal(2, provider.SearchCalls);
        Assert.Equal(2, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task Cache_expiry_rechecks_manager_and_catalog()
    {
        var provider = new SearchProvider("scoop", []);
        var clock = new SearchClock();
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"), clock);
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        var fresh = await service.SearchAsync("git", TestContext.Current.CancellationToken);
        Assert.False(fresh.Providers.Single().IsCached);
        Assert.Equal(2, provider.SearchCalls);
        Assert.Equal(2, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task Configuration_change_cannot_be_undone_by_late_cache_write()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SearchProvider("scoop", []) { Search = (_, ct) => pending.Task.WaitAsync(ct) };
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        var first = service.SearchAsync("git", TestContext.Current.CancellationToken);
        service.Invalidate("scoop");
        pending.SetResult([]);
        await first;
        Assert.False((await service.SearchAsync("git", TestContext.Current.CancellationToken)).Providers.Single().IsCached);
        Assert.Equal(2, provider.SearchCalls);
    }

    [Fact]
    public async Task Failed_refresh_never_resurrects_old_success()
    {
        var provider = new SearchProvider("scoop", [new("scoop", "git", "Git", "2.0", "main")]);
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        provider.Availability = ProviderAvailability.NotInstalled("Scoop was removed.");
        Assert.NotNull((await service.SearchAsync("git", TestContext.Current.CancellationToken, refresh: true)).Providers.Single().Error);
        var retry = await service.SearchAsync("git", TestContext.Current.CancellationToken);
        Assert.Empty(retry.Packages);
        Assert.NotNull(retry.Providers.Single().Error);
        Assert.False(retry.Providers.Single().IsCached);
    }

    [Fact]
    public async Task Provider_failure_is_retried_instead_of_cached()
    {
        var provider = new SearchProvider("scoop", []) { Search = (_, _) => throw new TimeoutException("Try again.") };
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.SearchCalls);
        Assert.Equal(2, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task Unavailable_manager_is_rechecked_after_short_delay()
    {
        var provider = new SearchProvider("scoop", []) { Availability = ProviderAvailability.NotInstalled("Missing.") };
        var clock = new SearchClock();
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"), clock);
        await service.SearchAsync("git", TestContext.Current.CancellationToken);
        provider.Availability = new(ProviderHealth.Available, "1.0", null);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Null((await service.SearchAsync("git", TestContext.Current.CancellationToken)).Providers.Single().Error);
        Assert.Equal(2, provider.AvailabilityCalls);
    }

    [Fact]
    public async Task Older_search_cannot_overwrite_a_newer_refresh()
    {
        var old = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new SearchProvider("scoop", [])
        {
            Search = (_, ct) => (Interlocked.Increment(ref calls) == 1 ? old.Task : fresh.Task).WaitAsync(ct),
        };
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"));
        var earlier = service.SearchAsync("git", TestContext.Current.CancellationToken);
        var later = service.SearchAsync("git", TestContext.Current.CancellationToken, refresh: true);
        fresh.SetResult([new("scoop", "git", "Git", "2.0", "main")]);
        await later;
        old.SetResult([new("scoop", "git", "Git", "1.0", "main")]);
        await earlier;
        var cached = await service.SearchAsync("git", TestContext.Current.CancellationToken);
        Assert.True(cached.Providers.Single().IsCached);
        Assert.Equal("2.0", cached.Packages.Single().Version);
    }

    [Fact]
    public async Task Query_cache_remains_bounded()
    {
        var provider = new SearchProvider("scoop", []);
        var clock = new SearchClock();
        var service = new PackageSearchService(new ProviderRegistry([provider]), new SearchSettings("scoop"), clock);
        for (var i = 0; i < 33; i++)
        {
            await service.SearchAsync("query" + i, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.False((await service.SearchAsync("query0", TestContext.Current.CancellationToken)).Providers.Single().IsCached);
        Assert.Equal(34, provider.SearchCalls);
    }

    private sealed class SearchClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SearchSettings(params string[] enabled) : ISettingsStore
    {
        public AppSettings Value { get; set; } = new() { EnabledProviders = enabled };
        public Task<AppSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class SearchProvider(string id, IReadOnlyList<PackageSearchResult> packages) : IUpdateProvider, IPackageSearchProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false);
        public ProviderAvailability Availability { get; set; } = new(ProviderHealth.Available, "1.0", null);
        public int AvailabilityCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public bool AvailabilityChecked { get; private set; }
        public string? LastQuery { get; private set; }
        public Func<string, CancellationToken, Task<IReadOnlyList<PackageSearchResult>>> Search { get; init; } = (_, _) => Task.FromResult(packages);
        public Task<ProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) { AvailabilityCalls++; AvailabilityChecked = true; return Task.FromResult(Availability); }
        public Task<IReadOnlyList<PackageSearchResult>> SearchPackagesAsync(string query, CancellationToken cancellationToken) { SearchCalls++; LastQuery = query; return Search(query, cancellationToken); }
        public Task<IReadOnlyList<InstalledApplication>> DiscoverAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
