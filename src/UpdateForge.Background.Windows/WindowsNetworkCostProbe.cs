using UpdateForge.Application;
using Windows.Networking.Connectivity;

namespace UpdateForge.Background;

/// <summary>Reads the Windows connection cost (Fixed/Variable = metered).</summary>
public sealed class WindowsNetworkCostProbe : INetworkCostProbe
{
    public Task<bool> IsMeteredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return Task.FromResult(cost?.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable);
        }
        catch (Exception) { return Task.FromResult(false); } // cannot determine: do not block
    }
}
