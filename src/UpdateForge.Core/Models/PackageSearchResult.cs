namespace UpdateForge.Core.Models;

public sealed record PackageSearchResult(string ProviderId, string PackageId, string Name, string Version, string? Source);
