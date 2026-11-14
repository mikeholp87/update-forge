using System.IO.Pipes;
using UpdateForge.Core.Storage;

namespace UpdateForge.Desktop.Services;

/// <summary>
/// One GUI per user. The first instance holds an exclusive lock file and listens on a local pipe; a second launch asks it to show its
/// window and exits. The OS drops the lock if the first instance dies, so a crash cannot lock the app out.
/// On Unix the socket lives in the per-user runtime folder rather than the shared temp folder, so another account cannot create the
/// name first; both ends also use <see cref="PipeOptions.CurrentUserOnly"/>, which refuses a peer running as a different user.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = UnixPipePath() ?? "UpdateForge.Gui.Show." + new string([.. Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    // A Unix socket path is limited to 108 bytes; a very long home folder falls back to a per-user name in the temp folder.
    private static string? UnixPipePath()
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(PrivateStorage.RuntimeDirectory(), "updateforge-gui.sock");
        return System.Text.Encoding.UTF8.GetByteCount(path) < 100 ? path : null;
    }
    private FileStream? _lock;
    private readonly CancellationTokenSource _stop = new();

    public static string LockPath => Path.Combine(PrivateStorage.RuntimeDirectory(), "updateforge-gui.lock");

    /// <summary>True when this process is the first instance.</summary>
    public bool TryBecomeFirst()
    {
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Tells the running instance to come to the foreground.</summary>
    public static void SignalFirst()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentException) { /* not listening yet, or not ours */ }
    }

    public void Listen(Action onShowRequested) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                onShowRequested();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500, CancellationToken.None).ConfigureAwait(false); }   // a client vanished mid-connect, or a foreign one was refused
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        _lock?.Dispose();
    }
}
