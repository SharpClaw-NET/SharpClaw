using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class VercelAIGatewayApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://gateway.ai.vercel.app/v1";
    public override string ProviderKey => WellKnownProviderKeys.VercelAIGateway;
}
