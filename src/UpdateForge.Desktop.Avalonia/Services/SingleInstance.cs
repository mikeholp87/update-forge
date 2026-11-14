using System.IO.Pipes;

namespace UpdateForge.Desktop.Services;

/// <summary>
/// One GUI per user. The first instance holds an exclusive lock file and listens on a local pipe; a second launch asks it to show its
/// window and exits. The OS drops the lock if the first instance dies, so a crash cannot lock the app out.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string PipeName = "UpdateForge.Gui.Show";
    private FileStream? _lock;
    private readonly CancellationTokenSource _stop = new();

    public static string LockPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Directory.Exists(r) ? r : Path.GetTempPath(), "updateforge-gui.lock");

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
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { /* the first instance is not listening yet */ }
    }

    public void Listen(Action onShowRequested) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                onShowRequested();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, CancellationToken.None).ConfigureAwait(false); }   // a client vanished mid-connect
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        _lock?.Dispose();
    }
}
