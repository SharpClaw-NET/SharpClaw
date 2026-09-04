using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
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
                $"No enabled provider registration registered provider '{providerKey}'.");
        var endpoint = configuration[$"Providers:{providerKey}:Endpoint"]
            ?? configuration["Provider:Endpoint"];
        var credential = configuration[$"Providers:{providerKey}:ApiKey"]
            ?? configuration["Provider:ApiKey"]
            ?? string.Empty;
        var options = new ProviderClientOptions(endpoint);
        if (!plugin.RequiresApiKey)
            return plugin.CreateClient(options);

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                $"Provider '{plugin.ProviderKey}' requires credentials, but no credentials are configured.");
        }

        if (plugin is not IProviderCredentialBoundPlugin credentialBound)
        {
            throw new InvalidOperationException(
                $"Provider '{plugin.ProviderKey}' requires credentials, but its plugin does not support host-side credential binding.");
        }

        return credentialBound.CreateClient(options, credential);
    }
}
