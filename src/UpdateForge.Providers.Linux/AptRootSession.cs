using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UpdateForge.Core.Processes;

namespace UpdateForge.Providers.Linux;

/// <summary>
/// One root helper (<c>pkexec updateforge-apt-helper serve</c>) kept for the whole session, so the password is asked once and not once
/// per package. Nothing here sees the password: polkit's agent asks for it when the helper starts. Commands go to the helper over
/// its stdin and answers come back on its stdout (framing is described in the helper script); no socket or file is involved, so
/// only this process can talk to it. The helper is the only thing that decides what runs as root, and it re-validates every request.
/// It ends when this object is disposed or the app exits (end of input), and after the idle limit; the next command then starts
/// a new one, which asks again. Commands run one at a time.
/// </summary>
internal sealed class AptRootSession : IDisposable
{
    /// <summary>The helper quits after 30 minutes without a request; this side retires a connection a little earlier so no request meets a closing helper.</summary>
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(25);

    private readonly IReadOnlyList<string> _command;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _conn;
    private DateTime _lastUse;
    private int _nextId;

    /// <param name="command">Full command line that starts the helper in serve mode (pkexec, the helper, <c>serve</c>).</param>
    internal AptRootSession(IReadOnlyList<string> command) => _command = command;

    /// <summary>
    /// Runs one helper request (for example <c>["upgrade", "vim=2:9.1-1"]</c>) and returns its combined output and exit code.
    /// Exit codes 126 and 127 with no output mean polkit refused or the dialog was dismissed, as with a one-off pkexec run.
    /// </summary>
    internal async Task<ProcessResult> RunAsync(IReadOnlyList<string> request, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken)
    {
        // One request is one line: a space or newline inside an argument would smuggle in a second request.
        if (request.Any(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))) throw new ArgumentException("Request arguments must not contain whitespace.", nameof(request));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            Connection? conn = null;
            Pending? pending = null;
            try
            {
                conn = Connect();
                await conn.Ready.Task.WaitAsync(limit.Token).ConfigureAwait(false);
                if (conn.Ended) return new ProcessResult(conn.ExitCode, "", conn.Stderr(), false);   // authorization refused or dismissed

                var id = ++_nextId;
                pending = new Pending(onLine);
                conn.Pending[id] = pending;
                await conn.Process.StandardInput.WriteLineAsync($"{id} {string.Join(' ', request)}".AsMemory(), limit.Token).ConfigureAwait(false);
                var code = await pending.Done.Task.WaitAsync(limit.Token).ConfigureAwait(false);
                _lastUse = DateTime.UtcNow;
                return new ProcessResult(code, pending.Text(), conn.Ended ? conn.Stderr() : "", false);
            }
            catch (OperationCanceledException)
            {
                // The helper is root and cannot be signalled; a command already sent runs to its end and its answer is dropped.
                // Before the helper has said "R" the process is still pkexec, which can be stopped (the dialog is closed).
                pending?.Abandon();
                if (pending is null && conn is { Ready.Task.IsCompleted: false }) conn.Kill();
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessResult(-1, pending?.Text() ?? "", "", true);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new ProcessResult(127, "", _command[0] + " could not be started: " + ex.Message, false);
            }
            catch (IOException)
            {
                conn?.Kill();   // the helper went away mid-request; the next command starts a new one
                return new ProcessResult(1, pending?.Text() ?? "", "The apt helper stopped unexpectedly. Try again.", false);
            }
        }
        finally { _gate.Release(); }
    }

    private Connection Connect()
    {
        if (_conn is { Ended: false } live && DateTime.UtcNow - _lastUse < IdleLimit) return live;
        _conn?.Close();

        var psi = new ProcessStartInfo(_command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = "/",
        };
        foreach (var a in _command.Skip(1)) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + _command[0] + ".");
        process.StandardInput.AutoFlush = true;
        _lastUse = DateTime.UtcNow;
        return _conn = new Connection(process);
    }

    public void Dispose()
    {
        _conn?.Close();
        _conn = null;
        _gate.Dispose();
    }

    /// <summary>A request that has been sent: its output so far and the exit code once the helper reports it.</summary>
    private sealed class Pending(Action<string>? onLine)
    {
        private readonly StringBuilder _text = new();
        private volatile bool _abandoned;

        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Abandon() => _abandoned = true;

        public void Add(string line)
        {
            lock (_text) _text.AppendLine(line);
            if (!_abandoned) onLine?.Invoke(line);
        }

        public string Text() { lock (_text) return _text.ToString(); }
    }

    private sealed class Connection
    {
        private readonly StringBuilder _stderr = new();

        public Connection(Process process)
        {
            Process = process;
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            _ = Task.Run(ReadAsync);
        }

        public Process Process { get; }
        public ConcurrentDictionary<int, Pending> Pending { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Ended;
        public int ExitCode { get; private set; }

        public string Stderr() { lock (_stderr) return _stderr.ToString(); }

        private async Task ReadAsync()
        {
            try
            {
                string? line;
                while ((line = await Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (line == "R") { Ready.TrySetResult(); continue; }
                    var part = line.Split(' ', 3);
                    if (part.Length < 2 || !int.TryParse(part[1], out var id) || !Pending.TryGetValue(id, out var p)) continue;
                    if (part[0] == "O") p.Add(part.Length == 3 ? part[2] : "");
                    else if (part[0] == "X" && part.Length == 3 && int.TryParse(part[2], out var code)) { Pending.TryRemove(id, out _); p.Done.TrySetResult(code); }
                }
            }
            catch (IOException) { /* the pipe closed under us: same as the helper ending */ }
            catch (ObjectDisposedException) { }

            var announced = Ready.Task.IsCompleted;
            try { await Process.WaitForExitAsync().ConfigureAwait(false); ExitCode = Process.ExitCode; } catch (InvalidOperationException) { ExitCode = 1; }
            Ended = true;
            Ready.TrySetResult();
            // Requests still waiting never got an answer (the helper quit). Success must never be inferred from that.
            foreach (var (_, p) in Pending) { p.Add("The apt helper stopped before finishing this request."); p.Done.TrySetResult(announced ? 1 : ExitCode); }
        }

        public void Kill() { try { Process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone, or root */ } }

        /// <summary>Ends the input: the helper finishes any running apt-get and exits. It is not killed; it is root and would not die, and a half-done upgrade is worse than waiting.</summary>
        public void Close() { try { Process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* already closed */ } }
    }
}
