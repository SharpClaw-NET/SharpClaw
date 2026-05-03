using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ContextTools.Services;

namespace SharpClaw.Modules.ContextTools;

/// <summary>
/// Default module: lightweight inline context tools that execute directly
/// in the ChatService streaming loop without creating job records.
/// Provides wait, list_accessible_threads, and read_thread_history.
/// </summary>
public sealed class ContextToolsModule : ISharpClawModule
{
    public string Id => "sharpclaw_context_tools";
    public string DisplayName => "Context Tools";
    public string ToolPrefix => "ct";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddScoped<ContextDataReader>();
        services.TryAddScoped<ContextToolsService>();
        services.AddScoped<IChatProcessingContributor, ContextToolsChatContributor>();
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    // ═══════════════════════════════════════════════════════════════
    // Global Flag Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new(ContextToolsPermissionKeys.CanReadCrossThreadHistory, "Read Cross-Thread History", "Read conversation history from other threads/channels.", "ReadCrossThreadHistoryAsync"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Job-pipeline Tool Definitions (none — all tools are inline)
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    // ═══════════════════════════════════════════════════════════════
    // Inline Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions()
    {
        // ReadCrossThreadHistory permission for context tools
        var crossThreadPerm = new ModuleToolPermission(
            IsPerResource: false, Check: null,
            DelegateTo: "ReadCrossThreadHistoryAsync");

        return
        [
            new("wait",
                "Pause for 1-300 seconds. No tokens consumed while waiting.",
                BuildWaitSchema()),

            new("list_accessible_threads",
                "List readable threads from other channels (IDs, names, parent channel).",
                BuildGlobalActionSchema(),
                crossThreadPerm),

            new("read_thread_history",
                "Read cross-channel thread history. Optional maxMessages (1-200, default 50).",
                BuildReadThreadHistorySchema(),
                crossThreadPerm),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Job-pipeline Tool Execution (unused — no job-pipeline tools)
    // ═══════════════════════════════════════════════════════════════

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        throw new InvalidOperationException(
            $"Context Tools module has no job-pipeline tools. Unknown: '{toolName}'.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Inline Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteInlineToolAsync(
        string toolName, JsonElement parameters, InlineToolContext context,
        IServiceProvider sp, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<ContextToolsService>();

        return toolName switch
        {
            "wait"
                => await ContextToolsService.WaitAsync(parameters, ct),

            "list_accessible_threads"
                => await svc.ListAccessibleThreadsAsync(
                    context.AgentId, context.ChannelId, ct),

            "read_thread_history"
                => await svc.ReadThreadHistoryAsync(
                    parameters, context.AgentId, context.ChannelId, ct),

            _ => throw new InvalidOperationException(
                $"Unknown Context Tools inline tool: '{toolName}'."),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct)
        => Task.CompletedTask;

    public Task ShutdownAsync() => Task.CompletedTask;

    // ═══════════════════════════════════════════════════════════════
    // Schema builders
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement BuildWaitSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "seconds": {
                        "type": "integer",
                        "description": "Seconds (1-300)."
                    }
                },
                "required": ["seconds"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildGlobalActionSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {},
                "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildReadThreadHistorySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "threadId": {
                        "type": "string",
                        "description": "Thread GUID (from list_accessible_threads)."
                    },
                    "maxMessages": {
                        "type": "integer",
                        "description": "Max messages (1-200, default 50)."
                    }
                },
                "required": ["threadId"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
