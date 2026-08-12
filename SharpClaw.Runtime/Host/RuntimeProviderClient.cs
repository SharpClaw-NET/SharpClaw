using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

public sealed class RuntimeProviderClientFactory : IRuntimeProviderClientFactory
{
    public IProviderApiClient Create(
        IConfiguration configuration,
        IReadOnlyList<IProviderPlugin> plugins)
    {
        var providerKey = configuration["Provider:Key"]
            ?? configuration["Providers:Default"]
            ?? throw new InvalidOperationException(
                "Provider:Key must be configured before a provider call.");
        var plugin = plugins.FirstOrDefault(value =>
            string.Equals(value.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No enabled provider module registered provider '{providerKey}'.");
        var endpoint = configuration[$"Providers:{providerKey}:Endpoint"]
            ?? configuration["Provider:Endpoint"];
        var credential = configuration[$"Providers:{providerKey}:ApiKey"]
            ?? configuration["Provider:ApiKey"]
            ?? string.Empty;
        return ProviderCredentialBinding.CreateClient(
            plugin,
            new ProviderClientOptions(endpoint),
            credential);
    }
}
