namespace UpdateForge.Core.Processes;

public static class PackageManagerOutput
{
    public static string RequireSuccess(ProcessResult result, string operation)
    {
        if (result.TimedOut) throw new TimeoutException(operation + " timed out.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed (exit code {result.ExitCode}). {result.StdErr.Trim()}");
        var output = result.StdOut.Trim();
        return output.Length == 0 ? operation + " completed; no output returned." : output;
    }
}
