using SharpClaw.Core;

namespace SharpClaw.Api.Services;

public sealed class BackendRegistry(IEnumerable<IAgentBackendProvider> providers)
{
    private readonly Dictionary<string, IAgentBackendProvider> _providers = BuildProviderMap(providers);

    public IReadOnlyList<string> BackendNames => _providers.Keys
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool TryGet(string backend, out IAgentBackendProvider provider)
        => _providers.TryGetValue(backend.Trim(), out provider!);

    public string BuildSupportedBackendsMessage(string fieldName = "backend")
    {
        var formattedNames = string.Join(", ", BackendNames.Select(name => $"'{name}'"));
        return $"{fieldName} must be {formattedNames}.";
    }

    private static Dictionary<string, IAgentBackendProvider> BuildProviderMap(IEnumerable<IAgentBackendProvider> providers)
    {
        var map = new Dictionary<string, IAgentBackendProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (!map.TryAdd(provider.BackendName, provider))
            {
                throw new InvalidOperationException(
                    $"Duplicate backend provider registration for '{provider.BackendName}'.");
            }
        }

        return map;
    }
}