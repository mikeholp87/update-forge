namespace UpdateForge.Core.Providers;

/// <summary>Optional maintenance operations supplied by an installed package manager.</summary>
public interface IPackageManager
{
    string ExecutablePath { get; }
    bool CanRefreshSources { get; }
    Task<string> GetSourcesAsync(CancellationToken cancellationToken);
    Task<string> RefreshSourcesAsync(CancellationToken cancellationToken);
}
