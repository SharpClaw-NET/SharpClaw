using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class OpenRouterApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://openrouter.ai/api/v1";
    public override string ProviderKey => WellKnownProviderKeys.OpenRouter;
}
