using UpdateForge.Core.Processes;
using UpdateForge.Core.Providers;
using UpdateForge.Core.Storage;
using UpdateForge.Providers.Linux;
using Xunit;

namespace UpdateForge.Core.Tests;

public class SecurityHardeningTests
{
    [Theory]
    [InlineData("lodash", true)]
    [InlineData("@anthropic-ai/claude-code", true)]
    [InlineData("socket.io", true)]
    [InlineData("evil.tgz", false)]
    [InlineData("evil.TAR", false)]
    [InlineData("x.tar.gz", false)]
    [InlineData("x.zip", false)]
    [InlineData("-g", false)]
    [InlineData("./x", false)]
    public void NpmNames_RejectArchivesThatToolsWouldInstallFromDisk(string name, bool valid) =>
        Assert.Equal(valid, NpmPackageName.IsValid(name));

    [Fact]
    public void FindUser_AsRoot_IgnoresUserWritableFolders()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("uf-cargo-");
        var name = "uf-fake-" + Guid.NewGuid().ToString("N");
        var old = Environment.GetEnvironmentVariable("CARGO_HOME");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
            var tool = Path.Combine(dir.FullName, "bin", name);
            File.WriteAllText(tool, "#!/bin/sh\n");
            Environment.SetEnvironmentVariable("CARGO_HOME", dir.FullName);

            Assert.Equal(tool, LinuxTools.FindUser(name, null, privileged: false));
            Assert.Null(LinuxTools.FindUser(name, null, privileged: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", old);
            dir.Delete(true);
        }
    }

    [Fact]
    public void Tighten_RemovesGroupAndOtherAccess()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("uf-data-");
        try
        {
            var file = Path.Combine(dir.FullName, "updateforge.db");
            var logs = Path.Combine(dir.FullName, "logs");
            File.WriteAllText(file, "");
            Directory.CreateDirectory(logs);
            File.SetUnixFileMode(dir.FullName, (UnixFileMode)0b111_111_101);   // 775
            File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);            // 644
            File.SetUnixFileMode(logs, (UnixFileMode)0b111_111_101);

            PrivateStorage.Tighten(dir.FullName);

            Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(dir.FullName));
            Assert.Equal((UnixFileMode)0b110_000_000, File.GetUnixFileMode(file));
            Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(logs));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ProcessRunner_DoesNotRunInTheCallersFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await new ProcessRunner().RunAsync(new ProcessSpec("/bin/pwd", [], TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.Equal("/", r.StdOut.Trim());
    }
}
