using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Groq exposes an OpenAI-compatible <c>/audio/transcriptions</c> endpoint.
/// </summary>
public sealed class GroqTranscriptionApiClient : OpenAiTranscriptionApiClient
{
    protected override string ApiEndpoint => "https://api.groq.com/openai/v1";
    public override ProviderType ProviderType => ProviderType.Groq;
}
