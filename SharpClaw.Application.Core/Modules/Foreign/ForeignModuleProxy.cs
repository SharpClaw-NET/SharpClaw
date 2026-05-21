using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed class ForeignModuleProxy(
    ModuleManifest manifest,
    ForeignModuleProtocolClient client,
    Func<Task> shutdown)
    : ISharpClawModule
{
    private IReadOnlyList<ForeignModuleToolDescriptor> _tools = [];
    private IReadOnlyList<ForeignModuleInlineToolDescriptor> _inlineTools = [];

    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;
    public string ToolPrefix => manifest.ToolPrefix;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [.. _tools.Select(tool => tool.ToModuleToolDefinition())];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
        [.. _inlineTools.Select(tool => tool.ToModuleInlineToolDefinition())];

    public void ApplyDiscovery(ForeignModuleDiscoveryResponse discovery)
    {
        _tools = discovery.Tools ?? [];
        _inlineTools = discovery.InlineTools ?? [];
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) =>
        client.InitializeAsync(manifest, ct);

    public Task ShutdownAsync() => shutdown();

    public async Task<ModuleHealthStatus> HealthCheckAsync(CancellationToken ct) =>
        (await client.HealthAsync(ct)).ToModuleHealthStatus();

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        ExecuteToolCoreAsync(toolName, parameters, job, ct);

    public ModuleJobCompletionBehavior GetJobCompletionBehavior(
        string toolName,
        JsonElement parameters,
        AgentJobContext job) =>
        _tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
            ?.CompletionBehavior
        ?? ModuleJobCompletionBehavior.CompleteWhenExecutionReturns;

    public Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        ExecuteInlineToolCoreAsync(toolName, parameters, context, ct);

    public IAsyncEnumerable<string>? ExecuteToolStreamingAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return tool?.SupportsStreaming == true
            ? client.ExecuteToolStreamingAsync(manifest, toolName, parameters, job, ct)
            : null;
    }

    private async Task<string> ExecuteToolCoreAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        CancellationToken ct)
    {
        var response = await client.ExecuteToolAsync(manifest, toolName, parameters, job, ct);
        return response.Result ?? string.Empty;
    }

    private async Task<string> ExecuteInlineToolCoreAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        CancellationToken ct)
    {
        var response = await client.ExecuteInlineToolAsync(manifest, toolName, parameters, context, ct);
        return response.Result ?? string.Empty;
    }
}
