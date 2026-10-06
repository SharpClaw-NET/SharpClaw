using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

internal static class RuntimeProviderSetup
{
    public const string RequiredErrorMessage = RuntimeChatConfiguration.RequiredErrorMessage;

    public static SharpClawProviderSetup Describe(
        IConfiguration configuration,
        RuntimeKernelAdapter adapter)
    {
        var plugins = (adapter.Graph.GetService(typeof(IEnumerable<IProviderPlugin>))
            as IEnumerable<IProviderPlugin>)?.ToArray() ?? [];
        var key = configuration["Provider:Key"] ?? configuration["Providers:Default"];
        var selected = plugins.SingleOrDefault(plugin =>
            string.Equals(plugin.ProviderKey, key, StringComparison.OrdinalIgnoreCase));
        var model = configuration["Provider:Model"];
        var required = adapter.Graph.Services.ProfileResolver is null &&
            (selected is null || string.IsNullOrWhiteSpace(model) ||
             (selected.RequiresApiKey && string.IsNullOrWhiteSpace(
                 configuration[$"Providers:{key}:ApiKey"] ?? configuration["Provider:ApiKey"])) ||
             (selected.RequiresEndpoint && string.IsNullOrWhiteSpace(
                 configuration[$"Providers:{key}:Endpoint"] ?? configuration["Provider:Endpoint"])));
        return new SharpClawProviderSetup(required, key, model,
            plugins.OrderBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(plugin => new SharpClawProviderSetupOption(
                    plugin.ProviderKey, plugin.DisplayName, plugin.RequiresApiKey, plugin.RequiresEndpoint))
                .ToArray());
    }
}
