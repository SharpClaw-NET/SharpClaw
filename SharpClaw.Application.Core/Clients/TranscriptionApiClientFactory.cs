using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class TranscriptionApiClientFactory
{
    private readonly Dictionary<ProviderType, ITranscriptionApiClient> _clients;

    public TranscriptionApiClientFactory(IEnumerable<ITranscriptionApiClient> clients)
    {
        _clients = clients.ToDictionary(c => c.ProviderType);
    }

    public ITranscriptionApiClient GetClient(ProviderType providerType)
    {
        return _clients.TryGetValue(providerType, out var client)
            ? client
            : throw new NotSupportedException(
                $"Provider type '{providerType}' does not support transcription.");
    }

    public bool Supports(ProviderType providerType) =>
        _clients.ContainsKey(providerType);
}
