namespace SharpClaw.Modules.Transcription.Clients;

public sealed class TranscriptionApiClientFactory
{
    private readonly Dictionary<string, ITranscriptionApiClient> _clients;
    private readonly ITranscriptionApiClient? _localClient;

    public TranscriptionApiClientFactory(IEnumerable<ITranscriptionApiClient> clients)
    {
        _localClient = null;
        _clients = [];
        foreach (var c in clients)
        {
            if (c.IsLocalInference)
                _localClient = c;
            else
                _clients[c.ProviderKey] = c;
        }
    }

    public ITranscriptionApiClient GetClient(string providerKey)
    {
        return _clients.TryGetValue(providerKey, out var client)
            ? client
            : throw new NotSupportedException(
                $"Provider key '{providerKey}' does not support transcription.");
    }

    public ITranscriptionApiClient GetLocalClient()
    {
        return _localClient
            ?? throw new NotSupportedException("No local transcription client is registered.");
    }

    public bool Supports(string providerKey) =>
        _clients.ContainsKey(providerKey);

    public bool SupportsLocal() => _localClient is not null;
}
