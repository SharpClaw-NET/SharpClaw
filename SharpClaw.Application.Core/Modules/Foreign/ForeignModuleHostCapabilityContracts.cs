using System.Text.Json;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed record ForeignModuleConfigGetRequest
{
    public string Key { get; init; } = string.Empty;
}

internal sealed record ForeignModuleConfigSetRequest
{
    public string Key { get; init; } = string.Empty;
    public string? Value { get; init; }
}

internal sealed record ForeignModuleConfigGetResponse(string? Value);

internal sealed record ForeignModuleConfigAllResponse(IReadOnlyDictionary<string, string> Values);

internal sealed record ForeignModuleLogRequest
{
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "Info";
}

internal sealed record ForeignModuleJobLogRequest
{
    public Guid JobId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "Info";
}

internal sealed record ForeignModuleJobCompleteRequest
{
    public Guid JobId { get; init; }
    public string? ResultData { get; init; }
    public string? Message { get; init; }
}

internal sealed record ForeignModuleJobFailRequest
{
    public Guid JobId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Details { get; init; }
}

internal sealed record ForeignModuleJobCancelRequest
{
    public Guid JobId { get; init; }
    public string? Message { get; init; }
}

internal sealed record ForeignModuleJobActionPrefixRequest
{
    public string ActionKeyPrefix { get; init; } = string.Empty;
    public Guid? ResourceId { get; init; }
}

internal sealed record ForeignModuleJobExistsWithActionPrefixRequest
{
    public Guid JobId { get; init; }
    public string ActionKeyPrefix { get; init; } = string.Empty;
}

internal sealed record ForeignModuleJobGetResponse(AgentJobResponse? Job);

internal sealed record ForeignModuleJobListResponse(IReadOnlyList<AgentJobResponse> Jobs);

internal sealed record ForeignModuleJobSummaryListResponse(IReadOnlyList<AgentJobSummaryResponse> Jobs);

internal sealed record ForeignModuleCapabilityAck(bool Accepted = true, string? Message = null);

internal sealed record ForeignModuleProtocolContractsListResponse(
    IReadOnlyList<ForeignModuleProtocolContractExport> Contracts);

internal sealed record ForeignModuleProtocolContractInvokeRequest
{
    public string ContractName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
}

internal sealed record ForeignModuleProtocolContractInvokeResponse(JsonElement Result);

internal sealed record ForeignModuleTaskSourceRequest
{
    public string SourceText { get; init; } = string.Empty;
}

internal sealed record ForeignModuleTaskIdRequest
{
    public Guid Id { get; init; }
}

internal sealed record ForeignModuleTaskUpdateRequest
{
    public Guid Id { get; init; }
    public string? SourceText { get; init; }
    public bool? IsActive { get; init; }
}

internal sealed record ForeignModuleTaskGetResponse(TaskDefinitionResponse? Definition);

internal sealed record ForeignModuleTaskListResponse(IReadOnlyList<TaskDefinitionResponse> Definitions);

internal sealed record ForeignModuleTaskDeleteResponse(bool Deleted);

internal sealed record ForeignModuleTaskLaunchRequest
{
    public Guid TaskDefinitionId { get; init; }
    public IReadOnlyDictionary<string, string>? ParameterValues { get; init; }
    public Guid? CallerAgentId { get; init; }
    public Guid? ChannelId { get; init; }
    public Guid? ContextId { get; init; }
}

internal sealed record ForeignModuleTaskLaunchResponse(Guid InstanceId);

internal sealed record ForeignModuleTaskContextExecuteStepsRequest
{
    public string ContextId { get; init; } = string.Empty;
    public Guid? ChannelId { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
    public IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor> Steps { get; init; } = [];
}

internal sealed record ForeignModuleTaskContextExecuteEventHandlerRequest
{
    public string HandlerId { get; init; } = string.Empty;
    public Guid? ChannelId { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
}

internal sealed record ForeignModuleTaskContextExecutionResponse(
    TaskStepResult Result,
    Guid ChannelId,
    IReadOnlyDictionary<string, JsonElement> Variables);

internal sealed record ForeignModuleIdsResponse(IReadOnlyList<Guid> Ids);

internal sealed record ForeignModuleLookupItemsResponse(IReadOnlyList<ForeignModuleLookupItem> Items);

internal sealed record ForeignModuleLookupItem(Guid Id, string Name);

internal sealed record ForeignModuleContextAccessibleThreadsRequest
{
    public Guid AgentId { get; init; }
    public Guid CurrentChannelId { get; init; }
    public string CrossThreadPermissionKey { get; init; } = string.Empty;
}

internal sealed record ForeignModuleContextThreadMessagesRequest
{
    public Guid ThreadId { get; init; }
    public int MaxMessages { get; init; } = 50;
}

internal sealed record ForeignModuleContextThreadsResponse(IReadOnlyList<ThreadSummary> Threads);

internal sealed record ForeignModuleContextMessagesResponse(
    IReadOnlyList<HostContextChatMessageSummary> Messages);

internal sealed record ForeignModuleConversationSteerResponse(
    ConversationSteeringResponse Steering);

internal sealed record ForeignModuleConversationSteeringListRequest
{
    public Guid ChannelId { get; init; }
    public Guid? ThreadId { get; init; }
    public int Limit { get; init; } = 20;
}

internal sealed record ForeignModuleConversationSteeringListResponse(
    IReadOnlyList<ConversationSteeringResponse> Steering);

internal sealed record ForeignModuleQueueMetricsResponse(
    double PendingJobCount,
    double PendingTaskCount,
    double SchedulerPendingJobCount);

internal sealed record ForeignModuleHostAgentChatRequest
{
    public Guid InstanceId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Guid? AgentId { get; init; }
}

internal sealed record ForeignModuleHostAgentChatToThreadRequest
{
    public Guid InstanceId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public Guid ThreadId { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? AgentId { get; init; }
}

internal sealed record ForeignModuleHostAgentTextResponse(string? Text);

internal sealed record ForeignModuleHostAgentParseStructuredResponseRequest
{
    public Guid InstanceId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? TypeName { get; init; }
}

internal sealed record ForeignModuleHostAgentFindRequest
{
    public string Search { get; init; } = string.Empty;
}

internal sealed record ForeignModuleHostAgentIdResponse(Guid? Id);

internal sealed record ForeignModuleHostAgentCreateAgentRequest
{
    public Guid InstanceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid ModelId { get; init; }
    public string? SystemPrompt { get; init; }
    public string? CustomId { get; init; }
}

internal sealed record ForeignModuleHostAgentCreateThreadRequest
{
    public Guid InstanceId { get; init; }
    public Guid? ChannelId { get; init; }
    public string? ThreadName { get; init; }
}

internal sealed record ForeignModuleHostAgentCreateRoleRequest
{
    public string RoleName { get; init; } = string.Empty;
}

internal sealed record ForeignModuleHostAgentSetRolePermissionsRequest
{
    public Guid RoleId { get; init; }
    public string RequestJson { get; init; } = string.Empty;
}

internal sealed record ForeignModuleHostAgentAssignRoleRequest
{
    public Guid AgentId { get; init; }
    public Guid RoleId { get; init; }
}

internal sealed record ForeignModuleHostAgentCreateChannelRequest
{
    public Guid InstanceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public Guid AgentId { get; init; }
    public string? CustomId { get; init; }
}

internal sealed record ForeignModuleHostAgentAddAllowedAgentRequest
{
    public Guid InstanceId { get; init; }
    public Guid AgentId { get; init; }
    public Guid? ChannelId { get; init; }
}

internal sealed record ForeignModuleAgentCreateRequest
{
    public string Name { get; init; } = string.Empty;
    public Guid ModelId { get; init; }
    public string? SystemPrompt { get; init; }
}

internal sealed record ForeignModuleAgentCreateResponse(
    Guid AgentId,
    string ModelName,
    string AgentName);

internal sealed record ForeignModuleAgentUpdateRequest
{
    public Guid AgentId { get; init; }
    public string? Name { get; init; }
    public string? SystemPrompt { get; init; }
    public Guid? ModelId { get; init; }
}

internal sealed record ForeignModuleAgentUpdateResponse(string Result);

internal sealed record ForeignModuleSetHeaderRequest
{
    public Guid Id { get; init; }
    public string? Header { get; init; }
}

internal sealed record ForeignModuleModelEnsureProviderRequest
{
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

internal sealed record ForeignModuleModelEnsureModelRequest
{
    public string ModelName { get; init; } = string.Empty;
    public Guid ProviderId { get; init; }
    public IReadOnlyList<string> CapabilityTags { get; init; } = [];
}

internal sealed record ForeignModuleModelMetadataRequest
{
    public Guid ModelId { get; init; }
}

internal sealed record ForeignModuleModelDeleteRequest
{
    public Guid ModelId { get; init; }
}

internal sealed record ForeignModuleGuidResponse(Guid Id);

internal sealed record ForeignModuleModelProviderInfoResponse(ModelProviderInfo? Info);

internal sealed record ForeignModuleModelLocalFilePathResponse(string? Path);

internal sealed record ForeignModuleModelMetadataResponse(ModelMetadata? Metadata);

internal sealed record ForeignModuleBooleanResponse(bool Value);

internal sealed record ForeignModuleExternalModulesRootResponse(string Directory);

internal sealed record ForeignModuleRegisteredRequest
{
    public string ModuleId { get; init; } = string.Empty;
}

internal sealed record ForeignModuleToolPrefixRegisteredRequest
{
    public string ToolPrefix { get; init; } = string.Empty;
}

internal sealed record ForeignModuleRegisteredResponse(bool IsRegistered);

internal sealed record ForeignModuleLoadRequest
{
    public string ModuleDir { get; init; } = string.Empty;
}

internal sealed record ForeignModuleModuleIdRequest
{
    public string ModuleId { get; init; } = string.Empty;
}

internal sealed record ForeignModuleStateResponseEnvelope(ModuleStateResponse State);

internal sealed record ForeignModuleInfoListResponse(IReadOnlyList<ModuleInfo> Modules);

internal sealed record ForeignModuleToolInvokeRequest
{
    public string ToolName { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
    public int? TimeoutSeconds { get; init; }
}

internal sealed record ForeignModuleToolInvokeResponse(string Result);

internal sealed record ForeignModuleStorageContractsResponse(
    IReadOnlyList<ModuleStorageContractDescriptor> Contracts);

internal sealed record ForeignModuleStorageInvokeRequest
{
    public string? ModuleId { get; init; }
    public string StorageName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
}

internal sealed record ForeignModuleStorageInvokeResponse(JsonElement Result);
