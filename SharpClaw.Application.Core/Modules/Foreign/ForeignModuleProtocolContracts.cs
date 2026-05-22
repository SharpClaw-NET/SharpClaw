using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

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
    IReadOnlyList<ForeignModuleInlineToolDescriptor>? InlineTools = null,
    IReadOnlyList<ForeignModuleProtocolContractExportDescriptor>? ProtocolContracts = null,
    IReadOnlyList<ForeignModuleProtocolContractRequirementDescriptor>? RequiredProtocolContracts = null,
    IReadOnlyList<ForeignModuleHeaderTagDescriptor>? HeaderTags = null,
    IReadOnlyList<ForeignModuleResourceTypeDescriptor>? ResourceTypes = null,
    IReadOnlyList<ForeignModuleGlobalFlagDescriptor>? GlobalFlags = null,
    IReadOnlyList<ModuleUiContribution>? UiContributions = null,
    IReadOnlyList<ModuleFrontendContribution>? FrontendContributions = null,
    IReadOnlyList<ForeignModuleCliCommandDescriptor>? CliCommands = null,
    ForeignModuleTaskParserDescriptor? TaskParser = null,
    IReadOnlyList<TaskStepDescriptor>? TaskStepDescriptors = null,
    IReadOnlyList<ForeignModuleTaskStepExecutorDescriptor>? TaskStepExecutors = null,
    IReadOnlyList<ForeignModuleTaskTriggerSourceDescriptor>? TaskTriggerSources = null,
    IReadOnlyList<ForeignModuleTaskTriggerBindingSideEffectDescriptor>? TaskTriggerBindingSideEffects = null,
    IReadOnlyList<ForeignModuleTaskMetricProviderDescriptor>? TaskMetricProviders = null,
    IReadOnlyList<ForeignModuleTaskEventSinkDescriptor>? TaskEventSinks = null);

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

public sealed record ForeignModuleHeaderTagDescriptor(
    string Name,
    bool SupportsContext = true)
{
    internal ModuleHeaderTag ToModuleHeaderTag(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client)
    {
        var tag = new ModuleHeaderTag(
            Name,
            async (_, ct) => (await client.ResolveHeaderTagAsync(
                manifest,
                Name,
                context: null,
                ct)).Value);

        return SupportsContext
            ? tag with
            {
                ResolveWithContext = async (_, context, ct) =>
                    (await client.ResolveHeaderTagAsync(
                        manifest,
                        Name,
                        context,
                        ct)).Value
            }
            : tag;
    }
}

public sealed record ForeignModuleResourceTypeDescriptor(
    string ResourceType,
    string GrantLabel,
    string DelegateMethodName,
    string? DefaultResourceKey = null,
    bool SupportsLookupItems = false)
{
    internal ModuleResourceTypeDescriptor ToModuleResourceTypeDescriptor(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client) =>
        new(
            ResourceType,
            GrantLabel,
            DelegateMethodName,
            async (_, ct) => await client.LoadResourceIdsAsync(
                manifest,
                ResourceType,
                ct),
            SupportsLookupItems
                ? async (_, ct) =>
                {
                    var items = await client.LoadResourceLookupItemsAsync(
                        manifest,
                        ResourceType,
                        ct);
                    return [.. items.Select(item => (item.Id, item.Name))];
                }
                : null,
            DefaultResourceKey);
}

public sealed record ForeignModuleGlobalFlagDescriptor(
    string FlagKey,
    string DisplayName,
    string Description,
    string DelegateMethodName)
{
    public ModuleGlobalFlagDescriptor ToModuleGlobalFlagDescriptor() =>
        new(FlagKey, DisplayName, Description, DelegateMethodName);
}

public sealed record ForeignModuleCliCommandDescriptor(
    string Name,
    string[]? Aliases,
    ModuleCliScope Scope,
    string Description,
    string[]? UsageLines)
{
    internal ModuleCliCommand ToModuleCliCommand(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client) =>
        new(
            Name,
            Aliases ?? [],
            Scope,
            Description,
            UsageLines ?? [],
            async (args, _, ct) =>
            {
                var result = await client.ExecuteCliCommandAsync(
                    manifest,
                    Name,
                    args,
                    ct);

                if (!string.IsNullOrEmpty(result.Stdout))
                    Console.Out.Write(result.Stdout);
                if (!string.IsNullOrEmpty(result.Stderr))
                    Console.Error.Write(result.Stderr);
            });
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

internal sealed record ForeignModuleHeaderTagResolveRequest(
    int ProtocolVersion,
    string ModuleId,
    string Name,
    ModuleHeaderTagContext? Context = null);

internal sealed record ForeignModuleHeaderTagResolveResponse(string Value);

internal sealed record ForeignModuleResourceRequest(
    int ProtocolVersion,
    string ModuleId,
    string ResourceType);

public sealed record ForeignModuleResourceLookupItem(Guid Id, string Name);

internal sealed record ForeignModuleResourceIdsResponse(IReadOnlyList<Guid> Ids);

internal sealed record ForeignModuleResourceLookupResponse(
    IReadOnlyList<ForeignModuleResourceLookupItem> Items);

internal sealed record ForeignModuleCliExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string CommandName,
    IReadOnlyList<string> Args);

internal sealed record ForeignModuleCliExecutionResponse(
    bool Success,
    string? Stdout = null,
    string? Stderr = null);

public sealed record ForeignModuleProtocolContractExportDescriptor(
    string ContractName,
    JsonElement Schema,
    IReadOnlyList<ForeignModuleProtocolContractOperation> Operations,
    string? Description = null)
{
    public ForeignModuleProtocolContractExport ToProtocolContractExport() =>
        new(ContractName, Schema, Operations, Description);
}

public sealed record ForeignModuleProtocolContractRequirementDescriptor(
    string ContractName,
    JsonElement? Schema = null,
    bool Optional = false,
    string? Description = null)
{
    public ForeignModuleProtocolContractRequirement ToProtocolContractRequirement() =>
        new(ContractName, Schema, Optional, Description);
}

internal sealed record ForeignModuleProtocolContractInvocationRequest(
    int ProtocolVersion,
    string ModuleId,
    string ContractName,
    string Operation,
    JsonElement Parameters);

internal sealed record ForeignModuleProtocolContractInvocationResponse(
    JsonElement Result);

public sealed record ForeignModuleTaskParserDescriptor(
    IReadOnlyList<ForeignModuleTaskParserStepMapping>? StepKeyMappings = null,
    IReadOnlyList<ForeignModuleTaskParserEventMapping>? EventTriggerMappings = null,
    IReadOnlyList<string>? SingleArgExpressionMethods = null,
    TaskParserPrimitives? Primitives = null,
    IReadOnlyList<ForeignModuleTaskTriggerAttributeHandlerDescriptor>? TriggerAttributeHandlers = null);

public sealed record ForeignModuleTaskParserStepMapping(
    string MethodName,
    string StepKey,
    string ModuleId);

public sealed record ForeignModuleTaskParserEventMapping(
    string MethodName,
    string TriggerKey,
    string ModuleId);

public sealed record ForeignModuleTaskTriggerAttributeHandlerDescriptor(
    string Name,
    IReadOnlyList<string>? NamedStringArgs = null,
    IReadOnlyList<string>? NamedIntArgs = null,
    IReadOnlyList<string>? NamedDoubleArgs = null);

public sealed record ForeignModuleTaskStepExecutorDescriptor(
    string ModuleId,
    IReadOnlyList<string> StepKeys,
    bool SupportsInvocation = false);

public sealed record ForeignModuleTaskTriggerSourceDescriptor(
    IReadOnlyList<string> TriggerKeys,
    bool OwnsBindingPersistence = false);

public sealed record ForeignModuleTaskTriggerBindingSideEffectDescriptor(
    string TriggerKey);

public sealed record ForeignModuleTaskMetricProviderDescriptor(
    string MetricName,
    string Description);

public sealed record ForeignModuleTaskEventSinkDescriptor(
    SharpClawEventType SubscribedEvents);

internal sealed record ForeignModuleTaskStepExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string StepKey,
    ForeignModuleTaskStepExecutionContextSnapshot Context,
    IReadOnlyList<string>? Arguments = null,
    string? Expression = null,
    string? ResultVariable = null);

internal sealed record ForeignModuleTaskStepInvocationRequest(
    int ProtocolVersion,
    string ModuleId,
    ForeignModuleTaskStepInvocationDescriptor Step,
    ForeignModuleTaskStepExecutionContextSnapshot Context);

internal sealed record ForeignModuleTaskStepExecutionContextSnapshot(
    Guid InstanceId,
    Guid ChannelId,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    IReadOnlyList<ForeignModuleTaskEventHandlerSnapshot>? EventHandlers = null);

internal sealed record ForeignModuleTaskEventHandlerSnapshot(
    string? ModuleTriggerKey,
    string? ParameterName);

internal sealed record ForeignModuleTaskStepInvocationDescriptor(
    string StepKey,
    string? VariableName = null,
    string? TypeName = null,
    string? ResultVariable = null,
    string? RawExpression = null,
    IReadOnlyList<string>? Arguments = null,
    string? ModuleTriggerKey = null,
    string? HandlerParameter = null,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor>? Body = null,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor>? ElseBody = null)
{
    public static ForeignModuleTaskStepInvocationDescriptor From(ITaskStepInvocation step) =>
        new(
            step.StepKey,
            step.VariableName,
            step.TypeName,
            step.ResultVariable,
            step.RawExpression,
            step.Arguments,
            step.ModuleTriggerKey,
            step.HandlerParameter,
            step.Body is null ? null : [.. step.Body.Select(From)],
            step.ElseBody is null ? null : [.. step.ElseBody.Select(From)]);
}

internal sealed record ForeignModuleTaskStepExecutionResponse(
    TaskStepResult Result = TaskStepResult.Continue,
    bool? Continue = null,
    IReadOnlyDictionary<string, JsonElement>? VariableUpdates = null,
    JsonElement? ResultVariableValue = null,
    IReadOnlyList<string>? Logs = null,
    string? OutputJson = null,
    Guid? ChannelId = null);

internal sealed record ForeignModuleTaskTriggerAttributeHandleRequest(
    int ProtocolVersion,
    string ModuleId,
    string HandlerName,
    ForeignModuleTaskTriggerAttributeContextDescriptor Context);

internal sealed record ForeignModuleTaskTriggerAttributeContextDescriptor(
    string AttributeName,
    int Line,
    int ArgumentCount,
    IReadOnlyList<string?> StringArgs,
    IReadOnlyList<int?> IntArgs,
    IReadOnlyList<string?> RawArgs,
    IReadOnlyDictionary<string, string?> NamedStringArgs,
    IReadOnlyDictionary<string, int?> NamedIntArgs,
    IReadOnlyDictionary<string, double?> NamedDoubleArgs);

internal sealed record ForeignModuleTaskTriggerAttributeHandleResponse(
    TaskTriggerDefinition? Trigger = null,
    IReadOnlyList<ForeignModuleTaskTriggerAttributeDiagnostic>? Diagnostics = null);

internal sealed record ForeignModuleTaskTriggerAttributeDiagnostic(
    TaskTriggerAttributeDiagnosticSeverity Severity,
    string Code,
    string Message);

internal sealed record ForeignModuleTaskTriggerStartRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    IReadOnlyList<ForeignModuleTaskTriggerSourceContextDescriptor> Contexts);

internal sealed record ForeignModuleTaskTriggerStopRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys);

internal sealed record ForeignModuleTaskTriggerSourceContextDescriptor(
    Guid TaskDefinitionId,
    TaskTriggerDefinition Definition);

internal sealed record ForeignModuleTaskTriggerDefinitionRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskTriggerDefinition Definition);

internal sealed record ForeignModuleTaskTriggerBindingValueResponse(
    string? Value);

internal sealed record ForeignModuleTaskTriggerSyncBindingsRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    TaskDefinitionDescriptor Definition,
    IReadOnlyList<TaskTriggerDefinition> OwnedTriggers);

internal sealed record ForeignModuleTaskTriggerSyncBindingsResponse(
    bool Changed);

internal sealed record ForeignModuleTaskTriggerRemoveBindingsRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    Guid DefinitionId);

internal sealed record ForeignModuleTaskTriggerBindingCreatedRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskDefinitionDescriptor Definition,
    TaskTriggerDefinition Trigger,
    TaskTriggerBindingDescriptor Binding);

internal sealed record ForeignModuleTaskTriggerBindingRemovedRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskTriggerBindingDescriptor Binding);

internal sealed record ForeignModuleTaskMetricValueRequest(
    int ProtocolVersion,
    string ModuleId,
    string MetricName);

internal sealed record ForeignModuleTaskMetricValueResponse(
    double Value);

internal sealed record ForeignModuleTaskEventSinkRequest(
    int ProtocolVersion,
    string ModuleId,
    SharpClawEvent Event);

internal sealed record ForeignModuleTaskAckResponse(
    bool Accepted = true);
