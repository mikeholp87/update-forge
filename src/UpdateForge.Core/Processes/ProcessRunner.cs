using System.Diagnostics;
using System.Text;

namespace UpdateForge.Core.Processes;

/// <summary>Fixed executable and strongly typed argument list. Never a shell command line.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, Action<string>? OnOutputLine = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            spec.OnOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.StandardInput.Close(); // never wait on interactive input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(spec.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.WaitForExit(); // flush async readers
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
        }
    }
}
