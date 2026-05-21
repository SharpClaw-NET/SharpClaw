using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed record ForeignModuleHandshakeRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolPrefix,
    string? HostVersion = null);

internal sealed record ForeignModuleHandshakeResponse(
    int ProtocolVersion,
    string ModuleId,
    string ToolPrefix,
    string Runtime,
    string RuntimeVersion,
    IReadOnlyList<string>? Capabilities = null);

internal sealed record ForeignModuleLifecycleRequest(
    int ProtocolVersion,
    string ModuleId);

internal sealed record ForeignModuleLifecycleResponse(
    bool Accepted = true,
    string? Message = null);

internal sealed record ForeignModuleHealthResponse(
    bool IsHealthy,
    string? Message = null,
    IReadOnlyDictionary<string, JsonElement>? Details = null)
{
    public ModuleHealthStatus ToModuleHealthStatus() =>
        new(IsHealthy, Message, Details?.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
}

public sealed record ForeignModuleDiscoveryResponse(
    IReadOnlyList<ForeignModuleEndpointDescriptor>? Endpoints = null,
    IReadOnlyList<ForeignModuleToolDescriptor>? Tools = null,
    IReadOnlyList<ForeignModuleInlineToolDescriptor>? InlineTools = null);

public sealed record ForeignModuleEndpointDescriptor(
    string Method,
    string RoutePattern,
    string ResponseMode,
    string? AuthPolicy = null,
    ForeignModulePermissionDescriptor? Permission = null,
    string? ContributionId = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record ForeignModulePermissionDescriptor(
    bool IsPerResource,
    string? DelegateTo = null)
{
    public ModuleToolPermission ToModuleToolPermission() =>
        string.IsNullOrWhiteSpace(DelegateTo)
            ? new ModuleToolPermission(
                IsPerResource,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        "Foreign module tool does not require host permission.",
                        PermissionClearance.Unset)))
            : new ModuleToolPermission(IsPerResource, Check: null, DelegateTo);
}

public sealed record ForeignModuleToolDescriptor(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    ForeignModulePermissionDescriptor? Permission = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? Aliases = null,
    bool SupportsStreaming = false,
    ModuleJobCompletionBehavior CompletionBehavior =
        ModuleJobCompletionBehavior.CompleteWhenExecutionReturns)
{
    public ModuleToolDefinition ToModuleToolDefinition() =>
        new(
            Name,
            Description,
            ParametersSchema,
            (Permission ?? new ForeignModulePermissionDescriptor(IsPerResource: false))
                .ToModuleToolPermission(),
            TimeoutSeconds,
            Aliases);
}

public sealed record ForeignModuleInlineToolDescriptor(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    ForeignModulePermissionDescriptor? Permission = null,
    IReadOnlyList<string>? Aliases = null)
{
    public ModuleInlineToolDefinition ToModuleInlineToolDefinition() =>
        new(
            Name,
            Description,
            ParametersSchema,
            Permission?.ToModuleToolPermission(),
            Aliases);
}

internal sealed record ForeignModuleToolExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleAgentJobContext Job);

internal sealed record ForeignModuleInlineToolExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleInlineToolContext Context);

internal sealed record ForeignModuleToolExecutionResponse(
    string? Result = null,
    ModuleJobCompletionBehavior? CompletionBehavior = null);

internal sealed record ForeignModuleAgentJobContext(
    Guid JobId,
    Guid AgentId,
    Guid ChannelId,
    Guid? ResourceId,
    string? ActionKey)
{
    public static ForeignModuleAgentJobContext From(AgentJobContext job) =>
        new(job.JobId, job.AgentId, job.ChannelId, job.ResourceId, job.ActionKey);
}

internal sealed record ForeignModuleInlineToolContext(
    Guid AgentId,
    Guid ChannelId,
    Guid? ThreadId,
    string ToolCallId)
{
    public static ForeignModuleInlineToolContext From(InlineToolContext context) =>
        new(context.AgentId, context.ChannelId, context.ThreadId, context.ToolCallId);
}

internal sealed record ForeignModuleToolStreamEvent(
    string? Delta = null,
    string? Result = null,
    string? Error = null,
    bool IsFinal = false);
