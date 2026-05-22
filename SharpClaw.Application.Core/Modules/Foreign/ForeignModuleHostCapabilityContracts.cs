using System.Text.Json;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;

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

internal sealed record ForeignModuleIdsResponse(IReadOnlyList<Guid> Ids);

internal sealed record ForeignModuleLookupItemsResponse(IReadOnlyList<ForeignModuleLookupItem> Items);

internal sealed record ForeignModuleLookupItem(Guid Id, string Name);

internal sealed record ForeignModuleQueueMetricsResponse(
    double PendingJobCount,
    double PendingTaskCount,
    double SchedulerPendingJobCount);

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
