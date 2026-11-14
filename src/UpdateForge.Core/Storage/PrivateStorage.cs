namespace UpdateForge.Core.Storage;

/// <summary>
/// Per-user data (inventory, history, logs, lock files) lists what is installed on the machine, so other local accounts should not read it.
/// On Linux/macOS the folders are created <c>0700</c> and files that already exist are tightened to owner-only; Windows keeps its default
/// per-user ACLs.
/// </summary>
public static class PrivateStorage
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// UpdateForge's per-user data folder (<c>~/.local/share/UpdateForge</c>, <c>%LOCALAPPDATA%\UpdateForge</c>). DoNotVerify matters:
    /// by default .NET returns "" when the base folder does not exist yet (a fresh account), which would put the data in the working folder.
    /// </summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "UpdateForge");

    /// <summary>Creates the folder (and missing parents) owner-only. An existing folder keeps its mode; use <see cref="Tighten"/> for ones this app owns.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, OwnerOnly);
    }

    /// <summary>Removes group and other access from <paramref name="path"/> and everything under it. Best effort: files owned by someone else are left alone.</summary>
    public static void Tighten(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
        Restrict(path);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if (entry.LinkTarget is null) Restrict(entry.FullName);
    }

    /// <summary>Owner-only access for one file or folder. Symbolic links are not followed.</summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            var tight = mode & ~GroupAndOther;
            if (tight != mode) File.SetUnixFileMode(path, tight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours to change, or already gone */ }
    }

    /// <summary>
    /// Folder for lock files and sockets: <c>$XDG_RUNTIME_DIR</c> (already per-user, <c>0700</c>) when it exists, otherwise a private
    /// folder under the user's data directory. Never the shared temp folder, where another account could hold the lock first.
    /// </summary>
    public static string RuntimeDirectory()
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Path.IsPathRooted(r) && Directory.Exists(r)) return r;
        var dir = Path.Combine(DataDirectory, "run");
        CreateDirectory(dir);
        return dir;
    }
}
