using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class MistralApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.mistral.ai/v1";
    public override string ProviderKey => WellKnownProviderKeys.Mistral;
}
