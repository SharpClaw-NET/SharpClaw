using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class ProviderApiClientFactory
{
    private readonly Dictionary<ProviderType, IProviderApiClient> _clients;

    public ProviderApiClientFactory(IEnumerable<IProviderApiClient> clients)
    {
        _clients = clients.ToDictionary(c => c.ProviderType);
    }

    /// <summary>
    /// Returns the API client for the given provider type.
    /// For <see cref="ProviderType.Custom"/>, supply the <paramref name="apiEndpoint"/>
    /// stored on the provider record.
    /// </summary>
    public IProviderApiClient GetClient(ProviderType providerType, string? apiEndpoint = null)
    {
        if (providerType == ProviderType.Custom)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiEndpoint, nameof(apiEndpoint));
            return new CustomOpenAiCompatibleApiClient(apiEndpoint);
        }

        // Ollama: use stored endpoint if provided, otherwise fall back to
        // the default (http://localhost:11434) baked into OllamaApiClient.
        if (providerType == ProviderType.Ollama)
            return new OllamaApiClient(apiEndpoint);

        return _clients.TryGetValue(providerType, out var client)
            ? client
            : throw new NotSupportedException($"Provider type '{providerType}' is not supported.");
    }
}
