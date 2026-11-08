using UpdateForge.Core.Models;
using UpdateForge.Providers.WinGet;
using Xunit;

namespace UpdateForge.Core.Tests;

public class WinGetParsingTests
{
    private static readonly HashSet<string> Ids = new(["Mozilla.Firefox", "TheDocumentFoundation.LibreOffice", "Rustlang.Rustup"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Sources = new(["winget", "msstore"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parses_export_json_with_multiple_sources()
    {
        const string json = """
            {"Sources":[{"Packages":[{"PackageIdentifier":"A.B","Version":"1.0"}],"SourceDetails":{"Name":"winget"}},
                        {"Packages":[{"PackageIdentifier":"XP123"}],"SourceDetails":{"Name":"msstore"}}]}
            """;
        var list = WinGetParsing.ParseExport(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("winget", list[0].Source);
        Assert.Equal("Unknown", list[1].Version);
    }

    [Fact]
    public void Parses_upgrade_rows_without_reading_headers()
    {
        // Header deliberately in another language: the parser must not care.
        const string text = """
            Nom                                  Id                                Version  Disponible Source
            -----------------------------------------------------------------------------------------------
            Mozilla Firefox (x64 fr)             Mozilla.Firefox                   144.0    145.0      winget
            LibreOffice 26.8.1.1                 TheDocumentFoundation.LibreOffice 26.8.1.1 26.8.2.2  winget
            2 mises à niveau disponibles.
            """;
        var rows = WinGetParsing.ParseRows(text, Ids, Sources);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Mozilla Firefox (x64 fr)", rows[0].Name);
        Assert.Equal("145.0", rows[0].Available);
        Assert.Equal("LibreOffice 26.8.1.1", rows[1].Name);
        Assert.Equal("26.8.2.2", rows[1].Available);
    }

    [Fact]
    public void Row_without_available_column_and_with_source()
    {
        var rows = WinGetParsing.ParseRows("Rustup   Rustlang.Rustup   1.29.2   winget", Ids, Sources);
        var row = Assert.Single(rows);
        Assert.Equal("1.29.2", row.Version);
        Assert.Null(row.Available);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void Row_without_source_column()
    {
        var row = Assert.Single(WinGetParsing.ParseRows("Rustup   Rustlang.Rustup   1.29.2", Ids, Sources));
        Assert.Equal("1.29.2", row.Version);
        Assert.Null(row.Source);
    }

    [Fact]
    public void Comparator_versions_are_kept_together()
    {
        var row = Assert.Single(WinGetParsing.ParseRows("Rustup   Rustlang.Rustup   < 1.29.2   winget", Ids, Sources));
        Assert.Equal("< 1.29.2", row.Version);
    }

    [Fact]
    public void Unknown_ids_and_noise_lines_are_ignored()
    {
        var rows = WinGetParsing.ParseRows("   - \\ | /\nSomething  Other.Package  1.0\n\n", Ids, Sources);
        Assert.Empty(rows);
    }

    [Fact]
    public void Local_arp_ids_are_recognised_as_anchors()
    {
        var row = Assert.Single(WinGetParsing.ParseRows(@"Old Tool   ARP\Machine\X64\OldTool   1.2.3", Ids, Sources));
        Assert.Equal("Old Tool", row.Name);
    }
}

public class WinGetExitCodeTests
{
    [Theory]
    [InlineData(0, UpdateOutcome.Succeeded)]
    [InlineData(unchecked((int)0x8A15002B), UpdateOutcome.NoChange)]
    [InlineData(unchecked((int)0x8A150109), UpdateOutcome.RestartRequired)]
    [InlineData(unchecked((int)0x8A150011), UpdateOutcome.Blocked)]   // hash mismatch
    [InlineData(unchecked((int)0x8A15002D), UpdateOutcome.Blocked)]   // security check failed
    [InlineData(unchecked((int)0x8A150068), UpdateOutcome.Blocked)]   // pinned
    [InlineData(unchecked((int)0x8A150019), UpdateOutcome.ManualActionRequired)] // needs admin
    [InlineData(unchecked((int)0x8A150101), UpdateOutcome.Failed)]    // app running
    [InlineData(unchecked((int)0x8A15010C), UpdateOutcome.Canceled)]
    [InlineData(1603, UpdateOutcome.Failed)]
    public void Maps_exit_codes(int code, UpdateOutcome expected) => Assert.Equal(expected, WinGetExitCodes.Map(code, null).Outcome);

    [Fact]
    public void Hash_mismatch_is_categorised_and_never_a_success()
    {
        var r = WinGetExitCodes.Map(unchecked((int)0x8A150011), null);
        Assert.Equal(ErrorCategory.HashMismatch, r.Error);
        Assert.NotEqual(UpdateOutcome.Succeeded, r.Outcome);
    }

    [Fact]
    public void Reboot_required_is_flagged() => Assert.True(WinGetExitCodes.Map(unchecked((int)0x8A150109), null).RestartRequired);
}

public class WinGetValidationTests
{
    private static UpdateRequest Req(string? id, string? source = "winget", string? version = null) =>
        new(new InstalledApplication { Id = "winget:x", Name = "x", ProviderId = "winget", PackageId = id, PackageSource = source }, version);

    [Theory]
    [InlineData("Mozilla.Firefox")]
    [InlineData("XP9KHM4BK9FZ7Q")]
    [InlineData("Microsoft.VisualStudioCode.Insiders")]
    public void Valid_ids_pass(string id) => Assert.Null(WinGetProvider.Validate(Req(id)));

    [Theory]
    [InlineData("Foo; calc.exe")]
    [InlineData("Foo && del C:\\*")]
    [InlineData("Foo|bar")]
    [InlineData("Foo\"bar")]
    [InlineData("--force")]
    [InlineData("-h")]
    [InlineData("..\\evil")]
    [InlineData("Foo Bar")]
    [InlineData("Foo\nBar")]
    [InlineData("$(whoami)")]
    [InlineData("")]
    [InlineData(null)]
    public void Injection_attempts_in_package_id_are_refused(string? id)
    {
        var r = WinGetProvider.Validate(Req(id));
        Assert.NotNull(r);
        Assert.Equal(ErrorCategory.SecurityBlocked, r!.Error);
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("https://evil.example/source")]
    [InlineData("winget --force")]
    [InlineData(null)]
    public void Unapproved_sources_are_refused(string? source) => Assert.NotNull(WinGetProvider.Validate(Req("A.B", source)));

    [Theory]
    [InlineData("1.0; calc")]
    [InlineData("--ignore-security-hash")]
    [InlineData("1.0 --force")]
    public void Malicious_target_versions_are_refused(string version) => Assert.NotNull(WinGetProvider.Validate(Req("A.B", "winget", version)));

    [Fact]
    public void Pinned_packages_are_blocked_not_run()
    {
        var req = new UpdateRequest(new InstalledApplication { Id = "w:a", Name = "a", ProviderId = "winget", PackageId = "A.B", PackageSource = "winget", IsPinned = true });
        Assert.Equal(UpdateOutcome.Blocked, WinGetProvider.Validate(req)!.Outcome);
    }
}
