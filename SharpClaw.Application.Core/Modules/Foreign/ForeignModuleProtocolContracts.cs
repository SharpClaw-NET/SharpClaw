using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
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
    IReadOnlyList<ModuleStorageContractDescriptor>? StorageContracts = null,
    IReadOnlyList<ForeignModuleCliCommandDescriptor>? CliCommands = null,
    ForeignModuleTaskParserDescriptor? TaskParser = null,
    IReadOnlyList<TaskStepDescriptor>? TaskStepDescriptors = null,
    IReadOnlyList<ForeignModuleTaskStepExecutorDescriptor>? TaskStepExecutors = null,
    IReadOnlyList<ForeignModuleTaskTriggerSourceDescriptor>? TaskTriggerSources = null,
    IReadOnlyList<ForeignModuleTaskTriggerBindingSideEffectDescriptor>? TaskTriggerBindingSideEffects = null,
    IReadOnlyList<ForeignModuleTaskMetricProviderDescriptor>? TaskMetricProviders = null,
    IReadOnlyList<ForeignModuleTaskEventSinkDescriptor>? TaskEventSinks = null,
    IReadOnlyList<ForeignModuleProviderPluginDescriptor>? ProviderPlugins = null);

public sealed record ForeignModuleEndpointDescriptor(
    string Method,
    string RoutePattern,
    string ResponseMode,
    string? AuthPolicy = null,
    ForeignModulePermissionDescriptor? Permission = null,
    string? ContributionId = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public static class ForeignModuleEndpointAuthPolicy
{
    public const string Anonymous = "anonymous";
    public const string Authenticated = "authenticated";
}

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
    bool SupportsDynamicCompletionBehavior = false,
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
    private static readonly JsonSerializerOptions CliJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal ModuleCliCommand ToModuleCliCommand(
        ModuleManifest manifest,
        ForeignModuleProtocolClient client) =>
        new(
            Name,
            Aliases ?? [],
            Scope,
            Description,
            UsageLines ?? [],
            async (args, sp, ct) =>
            {
                var result = await client.ExecuteCliCommandAsync(
                    manifest,
                    Name,
                    args,
                    ct);

                if (!string.IsNullOrEmpty(result.Stdout))
                    WriteStdout(result.Stdout, sp.GetService(typeof(ICliIdResolver)) as ICliIdResolver);
                if (!string.IsNullOrEmpty(result.Stderr))
                    Console.Error.Write(result.Stderr);
            });

    private static void WriteStdout(string stdout, ICliIdResolver? ids)
    {
        if (ids is null)
        {
            Console.Out.Write(stdout);
            return;
        }

        Console.Out.Write(RewriteJsonShortIds(stdout, ids));
    }

    private static string RewriteJsonShortIds(string text, ICliIdResolver ids)
    {
        var rewritten = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var start = FindNextJsonStart(text, index);
            if (start < 0)
            {
                rewritten.Append(text, index, text.Length - index);
                break;
            }

            rewritten.Append(text, index, start - index);

            if (!TryFindJsonEnd(text, start, out var end))
            {
                rewritten.Append(text, start, text.Length - start);
                break;
            }

            var raw = text[start..(end + 1)];
            try
            {
                var node = JsonNode.Parse(raw);
                if (node is null)
                {
                    rewritten.Append(raw);
                }
                else
                {
                    InjectShortIds(node, ids);
                    rewritten.Append(node.ToJsonString(CliJsonOptions));
                }
            }
            catch (JsonException)
            {
                rewritten.Append(raw);
            }

            index = end + 1;
        }

        return rewritten.ToString();
    }

    private static int FindNextJsonStart(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '{' or '[')
                return i;
        }

        return -1;
    }

    private static bool TryFindJsonEnd(string text, int start, out int end)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch is '{' or '[')
            {
                depth++;
                continue;
            }

            if (ch is not ('}' or ']'))
                continue;

            depth--;
            if (depth == 0)
            {
                end = i;
                return true;
            }
        }

        end = -1;
        return false;
    }

    private static void InjectShortIds(JsonNode node, ICliIdResolver ids)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("Id", out var idNode)
                && idNode is not null
                && Guid.TryParse(idNode.ToString(), out var guid))
            {
                var shortId = ids.GetOrAssign(guid);
                obj.Remove("#");

                var copy = new JsonObject { ["#"] = shortId };
                foreach (var kvp in obj.ToList())
                {
                    obj.Remove(kvp.Key);
                    copy[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in copy.ToList())
                {
                    copy.Remove(kvp.Key);
                    obj[kvp.Key] = kvp.Value;
                }
            }

            foreach (var prop in obj.ToList())
            {
                if (prop.Value is not null)
                    InjectShortIds(prop.Value, ids);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null)
                    InjectShortIds(item, ids);
            }
        }
    }
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

internal sealed record ForeignModuleToolCompletionBehaviorRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleAgentJobContext Job);

internal sealed record ForeignModuleToolCompletionBehaviorResponse(
    ModuleJobCompletionBehavior CompletionBehavior);

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
    IReadOnlyList<ForeignModuleTaskEventHandlerSnapshot>? EventHandlers = null,
    string? ContextCallbackId = null);

internal sealed record ForeignModuleTaskEventHandlerSnapshot(
    string? ModuleTriggerKey,
    string? ParameterName,
    string? HandlerCallbackId = null);

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
    Guid? ChannelId = null,
    IReadOnlyList<ForeignModuleTaskRegisteredEventHandlerDescriptor>? RegisteredEventHandlers = null);

internal sealed record ForeignModuleTaskRegisteredEventHandlerDescriptor(
    string ModuleTriggerKey,
    string? ParameterName,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor> Body);

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

public sealed record ForeignModuleProviderPluginDescriptor(
    string ProviderKey,
    string DisplayName,
    string? OwnerModuleId = null,
    bool RequiresEndpoint = false,
    bool SupportsAutomaticEndpointDiscovery = false,
    bool IsSeedable = true,
    bool RequiresApiKey = true,
    bool SupportsNativeToolCalling = false,
    IReadOnlyList<ProviderCostSeed>? CostSeeds = null,
    ForeignModuleCompletionParameterSpecDescriptor? ParameterSpec = null,
    bool SupportsDeviceCodeFlow = false,
    bool SupportsCostFeed = false,
    string? CostFeedPermissionDeniedNote = null);

public sealed record ForeignModuleCompletionParameterSpecDescriptor(
    string ProviderName,
    bool SupportsTemperature = true,
    float TemperatureMin = 0.0f,
    float TemperatureMax = 2.0f,
    bool SupportsTopP = true,
    float TopPMin = 0.0f,
    float TopPMax = 1.0f,
    bool SupportsTopK = true,
    int TopKMin = 1,
    int TopKMax = int.MaxValue,
    bool SupportsFrequencyPenalty = true,
    float FrequencyPenaltyMin = -2.0f,
    float FrequencyPenaltyMax = 2.0f,
    bool SupportsPresencePenalty = true,
    float PresencePenaltyMin = -2.0f,
    float PresencePenaltyMax = 2.0f,
    bool SupportsStop = true,
    int MaxStopSequences = 16,
    bool SupportsSeed = true,
    bool SupportsResponseFormat = true,
    bool RejectsJsonObjectResponseFormat = false,
    bool OnlyJsonObjectResponseFormat = false,
    bool SupportsReasoningEffort = true,
    bool ReasoningEffortInformationalOnly = false,
    string[]? ValidReasoningEffortValues = null,
    bool SupportsToolChoice = true,
    bool SupportsStrictTools = false)
{
    public static ForeignModuleCompletionParameterSpecDescriptor From(ICompletionParameterSpec spec) =>
        new(
            spec.ProviderName,
            spec.SupportsTemperature,
            spec.TemperatureMin,
            spec.TemperatureMax,
            spec.SupportsTopP,
            spec.TopPMin,
            spec.TopPMax,
            spec.SupportsTopK,
            spec.TopKMin,
            spec.TopKMax,
            spec.SupportsFrequencyPenalty,
            spec.FrequencyPenaltyMin,
            spec.FrequencyPenaltyMax,
            spec.SupportsPresencePenalty,
            spec.PresencePenaltyMin,
            spec.PresencePenaltyMax,
            spec.SupportsStop,
            spec.MaxStopSequences,
            spec.SupportsSeed,
            spec.SupportsResponseFormat,
            spec.RejectsJsonObjectResponseFormat,
            spec.OnlyJsonObjectResponseFormat,
            spec.SupportsReasoningEffort,
            spec.ReasoningEffortInformationalOnly,
            spec.ValidReasoningEffortValues,
            spec.SupportsToolChoice,
            spec.SupportsStrictTools);
}

internal sealed record ForeignModuleProviderModelListRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey);

internal sealed record ForeignModuleProviderModelListResponse(
    IReadOnlyList<string> ModelIds);

internal sealed record ForeignModuleProviderCapabilitiesResolveRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ModelName);

internal sealed record ForeignModuleProviderCapabilitiesResolveResponse(
    IReadOnlyList<string> Tags);

internal sealed record ForeignModuleProviderChatCompletionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<ChatCompletionMessage> Messages,
    int? MaxCompletionTokens = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    CompletionParameters? CompletionParameters = null);

internal sealed record ForeignModuleProviderChatCompletionWithToolsRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<ToolAwareMessage> Messages,
    IReadOnlyList<ChatToolDefinition> Tools,
    int? MaxCompletionTokens = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    CompletionParameters? CompletionParameters = null);

internal sealed record ForeignModuleProviderChatCompletionResponse(
    ChatCompletionResult Result);

internal sealed record ForeignModuleProviderDeviceCodeStartRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey);

internal sealed record ForeignModuleProviderDeviceCodeStartResponse(
    DeviceCodeSession Session);

internal sealed record ForeignModuleProviderDeviceCodePollRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    DeviceCodeSession Session);

internal sealed record ForeignModuleProviderDeviceCodePollResponse(
    string? AccessToken);

internal sealed record ForeignModuleProviderCostFeedRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ApiKey,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime);

internal sealed record ForeignModuleProviderCostFeedResponse(
    ProviderCostResult? Result);

internal sealed record ForeignModuleProviderAgentIdentifierSuffixRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ProviderName,
    Guid ModelId);

internal sealed record ForeignModuleProviderAgentIdentifierSuffixResponse(
    string Suffix);
