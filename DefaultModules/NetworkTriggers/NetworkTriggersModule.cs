using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.NetworkTriggers;

/// <summary>
/// Default module that owns the <c>HostReachable</c>, <c>HostUnreachable</c>,
/// and <c>NetworkChanged</c> task triggers. Scaffolded in Phase 1 of the
/// trigger-extraction plan; trigger source implementations move here in
/// Phase 3.
/// </summary>
public sealed class NetworkTriggersModule : ISharpClawModule, ITaskParserAware
{
    public ITaskParserModuleExtension ParserExtension => NetworkTriggersParserExtension.Instance;

    public string Id => "sharpclaw_network_triggers";
    public string DisplayName => "Network Triggers";
    public string ToolPrefix => "nettrigger";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ITaskTriggerSource, HostProbeTriggerSource>();
        services.AddSingleton<ITaskTriggerSource, NetworkTriggerSource>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            $"NetworkTriggers module has no job-pipeline tools. Unknown: '{toolName}'.");
}
