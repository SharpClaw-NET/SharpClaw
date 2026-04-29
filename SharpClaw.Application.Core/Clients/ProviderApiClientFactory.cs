using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class ProviderApiClientFactory
{
    private readonly Dictionary<string, IProviderApiClient> _clients;

    public ProviderApiClientFactory(IEnumerable<IProviderApiClient> clients)
    {
        _clients = clients.ToDictionary(c => c.ProviderKey);
    }

    /// <summary>
    /// Returns the API client for the given provider key.
    /// For <see cref="WellKnownProviderKeys.Custom"/>, supply the <paramref name="apiEndpoint"/>
    /// stored on the provider record.
    /// </summary>
    public IProviderApiClient GetClient(string providerKey, string? apiEndpoint = null)
    {
        if (providerKey == WellKnownProviderKeys.Custom)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiEndpoint, nameof(apiEndpoint));
            return new CustomOpenAiCompatibleApiClient(apiEndpoint);
        }

        // Ollama: use stored endpoint if provided, otherwise fall back to
        // the default (http://localhost:11434) baked into OllamaApiClient.
        if (providerKey == WellKnownProviderKeys.Ollama)
            return new OllamaApiClient(apiEndpoint);

        return _clients.TryGetValue(providerKey, out var client)
            ? client
            : throw new NotSupportedException($"Provider key '{providerKey}' is not supported.");
    }
}
