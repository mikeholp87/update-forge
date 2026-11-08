using UpdateForge.Core.Providers;

namespace UpdateForge.Application;

/// <summary>Providers are discovered through DI; adding one never requires changing UI or scheduler code.</summary>
public sealed class ProviderRegistry(IEnumerable<IUpdateProvider> providers)
{
    private readonly Dictionary<string, IUpdateProvider> _byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IUpdateProvider> All => _byId.Values;

    public IUpdateProvider? Find(string id) => _byId.GetValueOrDefault(id);
}
