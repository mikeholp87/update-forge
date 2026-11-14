using UpdateForge.Application;
using UpdateForge.Core.Processes;

namespace UpdateForge.Background;

/// <summary>Asks NetworkManager whether the connection is metered (<c>nmcli -t -f METERED general</c> prints yes, no, unknown, optionally "(guessed)").</summary>
public sealed class NmcliNetworkCostProbe(IProcessRunner runner, string nmcliPath = "/usr/bin/nmcli") : INetworkCostProbe
{
    public async Task<bool> IsMeteredAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(nmcliPath)) return false;
        try
        {
            var r = await runner.RunAsync(new ProcessSpec(nmcliPath, ["-t", "-f", "METERED", "general"], TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
            return r.ExitCode == 0 && r.StdOut.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; } // cannot determine: do not block
    }
}

/// <summary>No metered-connection signal available (non-NetworkManager systems and tests): never blocks.</summary>
public sealed class UnmeteredNetworkCostProbe : INetworkCostProbe
{
    public Task<bool> IsMeteredAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
