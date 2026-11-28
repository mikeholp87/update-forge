using UpdateForge.Application;
using UpdateForge.Infrastructure.Diagnostics;
using Xunit;

namespace UpdateForge.Core.Tests;

public class RedactorTests
{
    [Fact]
    public void Removes_profile_path_user_machine_and_email()
    {
        var text = @"Failed in C:\Users\alice\AppData\Local\UpdateForge on HOMEPC for alice@example.com";
        var r = Redactor.Redact(text, "alice", "HOMEPC", @"C:\Users\alice");
        Assert.DoesNotContain("alice", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HOMEPC", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", r, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Authorization: Bearer abc123")]
    [InlineData("api_key = sk-123456")]
    public void Removes_secrets(string text)
    {
        var r = Redactor.Redact(text, "zz", "zz", "C:\\none");
        Assert.Contains("<redacted>", r, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", r, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", r, StringComparison.Ordinal);
    }

    [Fact]
    public void Removes_long_hex_tokens() =>
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef01234567", Redactor.Redact("hash 0123456789abcdef0123456789abcdef01234567", "zz", "zz", "C:\\none"), StringComparison.Ordinal);
}

public class DiagnosticsSettingsTests
{
    [Fact]
    public void Dismissed_recommendations_are_counted_but_never_exported()
    {
        var json = DiagnosticsExporter.SettingsJson(new AppSettings { DismissedRecommendations = ["found:apt:secret-tool", "gamemode"] });
        Assert.DoesNotContain("secret-tool", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gamemode", json, StringComparison.Ordinal);
        Assert.Contains("2 entries (not exported)", json, StringComparison.Ordinal);
        Assert.Contains("\"Interval\"", json, StringComparison.Ordinal);   // the rest of the settings is still there for debugging
    }
}
