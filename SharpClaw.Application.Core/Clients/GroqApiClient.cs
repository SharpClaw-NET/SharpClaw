using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Clients;

public sealed class GroqApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.groq.com/openai/v1";
    public override string ProviderKey => WellKnownProviderKeys.Groq;
}
