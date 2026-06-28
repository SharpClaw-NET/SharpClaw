using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;

internal static class DotNetSidecarHostCapabilityProxies
{
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<ISharpClawEventSinkRegistry, NoOpEventSinkRegistry>();

        var client = DotNetSidecarHostCapabilityClient.TryCreateFromEnvironment();
        if (client is null)
            return;

        services.TryAddSingleton(client);
        services.TryAddSingleton<IModuleConfigStore, ModuleConfigStoreProxy>();
        services.TryAddSingleton<ITaskAuthoring, TaskAuthoringProxy>();
        services.TryAddSingleton<ITaskInstanceLauncher, TaskInstanceLauncherProxy>();
        services.TryAddSingleton<IHostQueueMetrics, HostQueueMetricsProxy>();
        services.TryAddSingleton<IHostAgentBridge, HostAgentBridgeProxy>();
        services.TryAddSingleton<ICoreEntityIdProvider, CoreEntityIdProviderProxy>();
        services.TryAddSingleton<IAgentManager, AgentManagerProxy>();
        services.TryAddSingleton<IModelInfoProvider, ModelInfoProviderProxy>();
        services.TryAddSingleton<IModelRegistrar, ModelRegistrarProxy>();
        services.TryAddSingleton<IModuleInfoProvider, ModuleInfoProviderProxy>();
        services.TryAddSingleton<IModuleLifecycleManager, ModuleLifecycleManagerProxy>();
        services.TryAddSingleton<IForeignModuleProtocolContractResolver, ProtocolContractResolverProxy>();
        services.TryAddSingleton<IModuleStorageGateway, ModuleStorageGatewayProxy>();
        services.TryAddSingleton<IAgentJobController, AgentJobControllerProxy>();
        services.TryAddSingleton<IAgentJobReader, AgentJobReaderProxy>();
    }

    private sealed class NoOpEventSinkRegistry : ISharpClawEventSinkRegistry
    {
        public void InvalidateCache()
        {
        }
    }

    private sealed class ModuleConfigStoreProxy(DotNetSidecarHostCapabilityClient client) : IModuleConfigStore
    {
        public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleConfigGetRequest, ForeignModuleConfigGetResponse>(
                ForeignModuleHostCapabilityProtocol.ConfigGetPath,
                new ForeignModuleConfigGetRequest { Key = key },
                ct)).Value;

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            where T : IParsable<T>
        {
            var value = await GetAsync(key, ct);
            return value is null || !T.TryParse(value, null, out var parsed)
                ? default
                : parsed;
        }

        public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.ConfigSetPath,
                new ForeignModuleConfigSetRequest { Key = key, Value = value },
                ct);

        public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            (await client.PostAsync<object, ForeignModuleConfigAllResponse>(
                ForeignModuleHostCapabilityProtocol.ConfigAllPath,
                new { },
                ct)).Values;
    }

    private sealed class TaskAuthoringProxy(DotNetSidecarHostCapabilityClient client) : ITaskAuthoring
    {
        public TaskValidationResponse ValidateDefinition(string sourceText) =>
            client.PostAsync<ForeignModuleTaskSourceRequest, TaskValidationResponse>(
                    ForeignModuleHostCapabilityProtocol.TaskValidatePath,
                    new ForeignModuleTaskSourceRequest { SourceText = sourceText },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public Task<TaskDefinitionResponse> CreateDefinitionAsync(
            CreateTaskDefinitionRequest request,
            CancellationToken ct = default) =>
            client.PostAsync<ForeignModuleTaskSourceRequest, TaskDefinitionResponse>(
                ForeignModuleHostCapabilityProtocol.TaskCreatePath,
                new ForeignModuleTaskSourceRequest { SourceText = request.SourceText },
                ct);

        public async Task<TaskDefinitionResponse?> GetDefinitionAsync(Guid id, CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleTaskIdRequest, ForeignModuleTaskGetResponse>(
                ForeignModuleHostCapabilityProtocol.TaskGetPath,
                new ForeignModuleTaskIdRequest { Id = id },
                ct)).Definition;

        public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(CancellationToken ct = default) =>
            (await client.PostAsync<object, ForeignModuleTaskListResponse>(
                ForeignModuleHostCapabilityProtocol.TaskListPath,
                new { },
                ct)).Definitions;

        public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
            Guid id,
            UpdateTaskDefinitionRequest request,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleTaskUpdateRequest, ForeignModuleTaskGetResponse>(
                ForeignModuleHostCapabilityProtocol.TaskUpdatePath,
                new ForeignModuleTaskUpdateRequest
                {
                    Id = id,
                    SourceText = request.SourceText,
                    IsActive = request.IsActive,
                },
                ct)).Definition;

        public async Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleTaskIdRequest, ForeignModuleTaskDeleteResponse>(
                ForeignModuleHostCapabilityProtocol.TaskDeletePath,
                new ForeignModuleTaskIdRequest { Id = id },
                ct)).Deleted;
    }

    private sealed class TaskInstanceLauncherProxy(DotNetSidecarHostCapabilityClient client) : ITaskInstanceLauncher
    {
        public async Task<Guid> LaunchAsync(
            Guid taskDefinitionId,
            IReadOnlyDictionary<string, string>? parameterValues,
            Guid? callerAgentId,
            Guid? channelId,
            Guid? contextId,
            CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleTaskLaunchRequest, ForeignModuleTaskLaunchResponse>(
                ForeignModuleHostCapabilityProtocol.TaskLaunchPath,
                new ForeignModuleTaskLaunchRequest
                {
                    TaskDefinitionId = taskDefinitionId,
                    ParameterValues = parameterValues,
                    CallerAgentId = callerAgentId,
                    ChannelId = channelId,
                    ContextId = contextId,
                },
                ct)).InstanceId;
    }

    private sealed class HostQueueMetricsProxy(DotNetSidecarHostCapabilityClient client) : IHostQueueMetrics
    {
        public async Task<double> GetPendingJobCountAsync(CancellationToken ct) =>
            (await ReadAsync(ct)).PendingJobCount;

        public async Task<double> GetPendingTaskCountAsync(CancellationToken ct) =>
            (await ReadAsync(ct)).PendingTaskCount;

        public async Task<double> GetSchedulerPendingJobCountAsync(CancellationToken ct) =>
            (await ReadAsync(ct)).SchedulerPendingJobCount;

        private Task<ForeignModuleQueueMetricsResponse> ReadAsync(CancellationToken ct) =>
            client.PostAsync<object, ForeignModuleQueueMetricsResponse>(
                ForeignModuleHostCapabilityProtocol.QueueMetricsPath,
                new { },
                ct);
    }

    private sealed class CoreEntityIdProviderProxy(DotNetSidecarHostCapabilityClient client) : ICoreEntityIdProvider
    {
        public async Task<List<Guid>> GetAgentIdsAsync(CancellationToken ct = default) =>
            [.. (await client.PostAsync<object, ForeignModuleIdsResponse>(
                ForeignModuleHostCapabilityProtocol.CoreAgentIdsPath,
                new { },
                ct)).Ids];

        public async Task<List<Guid>> GetChannelIdsAsync(CancellationToken ct = default) =>
            [.. (await client.PostAsync<object, ForeignModuleIdsResponse>(
                ForeignModuleHostCapabilityProtocol.CoreChannelIdsPath,
                new { },
                ct)).Ids];

        public async Task<List<(Guid Id, string Name)>> GetAgentLookupItemsAsync(CancellationToken ct = default) =>
            [.. (await client.PostAsync<object, ForeignModuleLookupItemsResponse>(
                ForeignModuleHostCapabilityProtocol.CoreAgentLookupPath,
                new { },
                ct)).Items.Select(item => (item.Id, item.Name))];

        public async Task<List<(Guid Id, string Name)>> GetChannelLookupItemsAsync(CancellationToken ct = default) =>
            [.. (await client.PostAsync<object, ForeignModuleLookupItemsResponse>(
                ForeignModuleHostCapabilityProtocol.CoreChannelLookupPath,
                new { },
                ct)).Items.Select(item => (item.Id, item.Name))];
    }

    private sealed class HostAgentBridgeProxy(DotNetSidecarHostCapabilityClient client) : IHostAgentBridge
    {
        public async Task<string?> ChatAsync(
            Guid instanceId,
            string taskName,
            string message,
            Guid? agentId,
            CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleHostAgentChatRequest, ForeignModuleHostAgentTextResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentChatPath,
                new ForeignModuleHostAgentChatRequest
                {
                    InstanceId = instanceId,
                    TaskName = taskName,
                    Message = message,
                    AgentId = agentId,
                },
                ct)).Text;

        public async Task<string> ChatStreamAsync(
            Guid instanceId,
            string taskName,
            string message,
            Guid? agentId,
            CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleHostAgentChatRequest, ForeignModuleHostAgentTextResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentChatStreamPath,
                new ForeignModuleHostAgentChatRequest
                {
                    InstanceId = instanceId,
                    TaskName = taskName,
                    Message = message,
                    AgentId = agentId,
                },
                ct)).Text ?? string.Empty;

        public async Task<string?> ChatToThreadAsync(
            Guid instanceId,
            string taskName,
            Guid threadId,
            string message,
            Guid? agentId,
            CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleHostAgentChatToThreadRequest, ForeignModuleHostAgentTextResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentChatToThreadPath,
                new ForeignModuleHostAgentChatToThreadRequest
                {
                    InstanceId = instanceId,
                    TaskName = taskName,
                    ThreadId = threadId,
                    Message = message,
                    AgentId = agentId,
                },
                ct)).Text;

        public string ParseStructuredResponse(Guid instanceId, string text, string? typeName) =>
            client.PostAsync<ForeignModuleHostAgentParseStructuredResponseRequest, ForeignModuleHostAgentTextResponse>(
                    ForeignModuleHostCapabilityProtocol.HostAgentParseStructuredResponsePath,
                    new ForeignModuleHostAgentParseStructuredResponseRequest
                    {
                        InstanceId = instanceId,
                        Text = text,
                        TypeName = typeName,
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Text ?? string.Empty;

        public Task<Guid?> FindModelAsync(string search, CancellationToken ct) =>
            FindAsync(ForeignModuleHostCapabilityProtocol.HostAgentFindModelPath, search, ct);

        public Task<Guid?> FindProviderAsync(string search, CancellationToken ct) =>
            FindAsync(ForeignModuleHostCapabilityProtocol.HostAgentFindProviderPath, search, ct);

        public Task<Guid?> FindAgentAsync(string search, CancellationToken ct) =>
            FindAsync(ForeignModuleHostCapabilityProtocol.HostAgentFindAgentPath, search, ct);

        public Task<Guid?> FindRoleAsync(string search, CancellationToken ct) =>
            FindAsync(ForeignModuleHostCapabilityProtocol.HostAgentFindRolePath, search, ct);

        public Task<Guid?> FindChannelAsync(string search, CancellationToken ct) =>
            FindAsync(ForeignModuleHostCapabilityProtocol.HostAgentFindChannelPath, search, ct);

        public async Task<Guid> CreateAgentAsync(
            Guid instanceId,
            string name,
            Guid modelId,
            string? systemPrompt,
            string? customId,
            CancellationToken ct) =>
            RequireId(await client.PostAsync<ForeignModuleHostAgentCreateAgentRequest, ForeignModuleHostAgentIdResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentCreateAgentPath,
                new ForeignModuleHostAgentCreateAgentRequest
                {
                    InstanceId = instanceId,
                    Name = name,
                    ModelId = modelId,
                    SystemPrompt = systemPrompt,
                    CustomId = customId,
                },
                ct));

        public async Task<Guid> CreateThreadAsync(
            Guid instanceId,
            Guid? channelId,
            string? threadName,
            CancellationToken ct) =>
            RequireId(await client.PostAsync<ForeignModuleHostAgentCreateThreadRequest, ForeignModuleHostAgentIdResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentCreateThreadPath,
                new ForeignModuleHostAgentCreateThreadRequest
                {
                    InstanceId = instanceId,
                    ChannelId = channelId,
                    ThreadName = threadName,
                },
                ct));

        public async Task<Guid> CreateRoleAsync(string roleName, CancellationToken ct) =>
            RequireId(await client.PostAsync<ForeignModuleHostAgentCreateRoleRequest, ForeignModuleHostAgentIdResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentCreateRolePath,
                new ForeignModuleHostAgentCreateRoleRequest { RoleName = roleName },
                ct));

        public Task SetRolePermissionsAsync(
            Guid roleId,
            string requestJson,
            CancellationToken ct) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.HostAgentSetRolePermissionsPath,
                new ForeignModuleHostAgentSetRolePermissionsRequest
                {
                    RoleId = roleId,
                    RequestJson = requestJson,
                },
                ct);

        public Task AssignRoleAsync(
            Guid agentId,
            Guid roleId,
            CancellationToken ct) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.HostAgentAssignRolePath,
                new ForeignModuleHostAgentAssignRoleRequest
                {
                    AgentId = agentId,
                    RoleId = roleId,
                },
                ct);

        public async Task<Guid> CreateChannelAsync(
            Guid instanceId,
            string title,
            Guid agentId,
            string? customId,
            CancellationToken ct) =>
            RequireId(await client.PostAsync<ForeignModuleHostAgentCreateChannelRequest, ForeignModuleHostAgentIdResponse>(
                ForeignModuleHostCapabilityProtocol.HostAgentCreateChannelPath,
                new ForeignModuleHostAgentCreateChannelRequest
                {
                    InstanceId = instanceId,
                    Title = title,
                    AgentId = agentId,
                    CustomId = customId,
                },
                ct));

        public Task AddAllowedAgentAsync(
            Guid instanceId,
            Guid agentId,
            Guid? channelId,
            CancellationToken ct) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.HostAgentAddAllowedAgentPath,
                new ForeignModuleHostAgentAddAllowedAgentRequest
                {
                    InstanceId = instanceId,
                    AgentId = agentId,
                    ChannelId = channelId,
                },
                ct);

        private async Task<Guid?> FindAsync(string path, string search, CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleHostAgentFindRequest, ForeignModuleHostAgentIdResponse>(
                path,
                new ForeignModuleHostAgentFindRequest { Search = search },
                ct)).Id;

        private static Guid RequireId(ForeignModuleHostAgentIdResponse response) =>
            response.Id ?? throw new InvalidOperationException("SharpClaw host returned an empty ID.");
    }

    private sealed class AgentManagerProxy(DotNetSidecarHostCapabilityClient client) : IAgentManager
    {
        public async Task<(Guid AgentId, string ModelName, string AgentName)> CreateSubAgentAsync(
            string name,
            Guid modelId,
            string? systemPrompt,
            CancellationToken ct = default)
        {
            var response = await client.PostAsync<ForeignModuleAgentCreateRequest, ForeignModuleAgentCreateResponse>(
                ForeignModuleHostCapabilityProtocol.AgentCreateSubAgentPath,
                new ForeignModuleAgentCreateRequest
                {
                    Name = name,
                    ModelId = modelId,
                    SystemPrompt = systemPrompt,
                },
                ct);
            return (response.AgentId, response.ModelName, response.AgentName);
        }

        public async Task<string> UpdateAgentAsync(
            Guid agentId,
            string? name,
            string? systemPrompt,
            Guid? modelId,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleAgentUpdateRequest, ForeignModuleAgentUpdateResponse>(
                ForeignModuleHostCapabilityProtocol.AgentUpdatePath,
                new ForeignModuleAgentUpdateRequest
                {
                    AgentId = agentId,
                    Name = name,
                    SystemPrompt = systemPrompt,
                    ModelId = modelId,
                },
                ct)).Result;

        public Task SetAgentHeaderAsync(Guid agentId, string? header, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.AgentSetHeaderPath,
                new ForeignModuleSetHeaderRequest { Id = agentId, Header = header },
                ct);

        public Task SetChannelHeaderAsync(Guid channelId, string? header, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.ChannelSetHeaderPath,
                new ForeignModuleSetHeaderRequest { Id = channelId, Header = header },
                ct);
    }

    private sealed class ModuleInfoProviderProxy(DotNetSidecarHostCapabilityClient client) : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() =>
            client.PostAsync<object, ForeignModuleInfoListResponse>(
                    ForeignModuleHostCapabilityProtocol.ModulesInfoListPath,
                    new { },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Modules;
    }

    private sealed class ModelInfoProviderProxy(DotNetSidecarHostCapabilityClient client) : IModelInfoProvider
    {
        public async Task<ModelProviderInfo?> GetModelProviderInfoAsync(
            Guid modelId,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelMetadataRequest, ForeignModuleModelProviderInfoResponse>(
                ForeignModuleHostCapabilityProtocol.ModelProviderInfoPath,
                new ForeignModuleModelMetadataRequest { ModelId = modelId },
                ct)).Info;

        public async Task<string?> GetLocalModelFilePathAsync(
            Guid modelId,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelMetadataRequest, ForeignModuleModelLocalFilePathResponse>(
                ForeignModuleHostCapabilityProtocol.ModelLocalFilePathPath,
                new ForeignModuleModelMetadataRequest { ModelId = modelId },
                ct)).Path;
    }

    private sealed class ModelRegistrarProxy(DotNetSidecarHostCapabilityClient client) : IModelRegistrar
    {
        public async Task<Guid> EnsureProviderAsync(
            string providerKey,
            string displayName,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelEnsureProviderRequest, ForeignModuleGuidResponse>(
                ForeignModuleHostCapabilityProtocol.ModelEnsureProviderPath,
                new ForeignModuleModelEnsureProviderRequest
                {
                    ProviderKey = providerKey,
                    DisplayName = displayName,
                },
                ct)).Id;

        public async Task<Guid> EnsureModelAsync(
            string modelName,
            Guid providerId,
            IReadOnlyList<string> capabilityTags,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelEnsureModelRequest, ForeignModuleGuidResponse>(
                ForeignModuleHostCapabilityProtocol.ModelEnsureModelPath,
                new ForeignModuleModelEnsureModelRequest
                {
                    ModelName = modelName,
                    ProviderId = providerId,
                    CapabilityTags = capabilityTags,
                },
                ct)).Id;

        public async Task<ModelMetadata?> GetModelMetadataAsync(
            Guid modelId,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelMetadataRequest, ForeignModuleModelMetadataResponse>(
                ForeignModuleHostCapabilityProtocol.ModelMetadataPath,
                new ForeignModuleModelMetadataRequest { ModelId = modelId },
                ct)).Metadata;

        public async Task<bool> DeleteModelAsync(Guid modelId, CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModelDeleteRequest, ForeignModuleBooleanResponse>(
                ForeignModuleHostCapabilityProtocol.ModelDeletePath,
                new ForeignModuleModelDeleteRequest { ModelId = modelId },
                ct)).Value;
    }

    private sealed class ModuleLifecycleManagerProxy(DotNetSidecarHostCapabilityClient client) : IModuleLifecycleManager
    {
        private readonly RemoteToolModule _remoteToolModule = new(client);
        private string? _externalModulesDir;

        public string ExternalModulesDir =>
            _externalModulesDir ??= client.PostAsync<object, ForeignModuleExternalModulesRootResponse>(
                    ForeignModuleHostCapabilityProtocol.ModulesExternalRootPath,
                    new { },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Directory;

        public bool IsModuleRegistered(string moduleId) =>
            client.PostAsync<ForeignModuleRegisteredRequest, ForeignModuleRegisteredResponse>(
                    ForeignModuleHostCapabilityProtocol.ModuleRegisteredPath,
                    new ForeignModuleRegisteredRequest { ModuleId = moduleId },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .IsRegistered;

        public bool IsToolPrefixRegistered(string toolPrefix) =>
            client.PostAsync<ForeignModuleToolPrefixRegisteredRequest, ForeignModuleRegisteredResponse>(
                    ForeignModuleHostCapabilityProtocol.ModuleToolPrefixRegisteredPath,
                    new ForeignModuleToolPrefixRegisteredRequest { ToolPrefix = toolPrefix },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .IsRegistered;

        public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) =>
            (_remoteToolModule, toolName);

        public async Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleLoadRequest, ForeignModuleStateResponseEnvelope>(
                ForeignModuleHostCapabilityProtocol.ModuleLoadPath,
                new ForeignModuleLoadRequest { ModuleDir = moduleDir },
                ct)).State;

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.ModuleUnloadPath,
                new ForeignModuleModuleIdRequest { ModuleId = moduleId },
                ct);

        public async Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleModuleIdRequest, ForeignModuleStateResponseEnvelope>(
                ForeignModuleHostCapabilityProtocol.ModuleReloadPath,
                new ForeignModuleModuleIdRequest { ModuleId = moduleId },
                ct)).State;
    }

    private sealed class ProtocolContractResolverProxy(DotNetSidecarHostCapabilityClient client)
        : IForeignModuleProtocolContractResolver
    {
        public IForeignModuleProtocolContractInvoker? Resolve(string contractName)
        {
            var export = GetAllExports()
                .FirstOrDefault(candidate => string.Equals(candidate.ContractName, contractName, StringComparison.Ordinal));
            return export is null ? null : new ProtocolContractInvokerProxy(client, export);
        }

        public IReadOnlyList<ForeignModuleProtocolContractExport> GetAllExports() =>
            client.PostAsync<object, ForeignModuleProtocolContractsListResponse>(
                    ForeignModuleHostCapabilityProtocol.ProtocolContractsListPath,
                    new { },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Contracts;
    }

    private sealed class ModuleStorageGatewayProxy(DotNetSidecarHostCapabilityClient client) : IModuleStorageGateway
    {
        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
            client.PostAsync<object, ForeignModuleStorageContractsResponse>(
                    ForeignModuleHostCapabilityProtocol.ModuleStorageListPath,
                    new { },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Contracts;

        public async Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleStorageInvokeRequest, ForeignModuleStorageInvokeResponse>(
                ForeignModuleHostCapabilityProtocol.ModuleStorageInvokePath,
                new ForeignModuleStorageInvokeRequest
                {
                    ModuleId = moduleId,
                    StorageName = storageName,
                    Operation = operation,
                    Parameters = parameters,
                },
                ct)).Result;
    }

    private sealed class AgentJobControllerProxy(DotNetSidecarHostCapabilityClient client) : IAgentJobController
    {
        public Task<AgentJobResponse> SubmitJobAsync(
            Guid channelId,
            SubmitAgentJobRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Submitting new jobs from a .NET sidecar is not exposed yet.");

        public Task<AgentJobResponse?> StopJobAsync(
            Guid jobId,
            string? requiredActionPrefix = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Stopping arbitrary jobs from a .NET sidecar is not exposed yet.");

        public Task AddJobLogAsync(
            Guid jobId,
            string message,
            string level = JobLogLevels.Info,
            CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobLogPath,
                new ForeignModuleJobLogRequest
                {
                    JobId = jobId,
                    Message = message,
                    Level = level,
                },
                ct);

        public Task MarkJobCompletedAsync(
            Guid jobId,
            string? resultData = null,
            string? message = null,
            CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobCompletePath,
                new ForeignModuleJobCompleteRequest
                {
                    JobId = jobId,
                    ResultData = resultData,
                    Message = message,
                },
                ct);

        public Task MarkJobFailedAsync(Guid jobId, Exception exception, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobFailPath,
                new ForeignModuleJobFailRequest
                {
                    JobId = jobId,
                    Message = exception.Message,
                    Details = exception.ToString(),
                },
                ct);

        public Task MarkJobFailedAsync(
            Guid jobId,
            string message,
            string? details = null,
            CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobFailPath,
                new ForeignModuleJobFailRequest
                {
                    JobId = jobId,
                    Message = message,
                    Details = details,
                },
                ct);

        public Task MarkJobCancelledAsync(
            Guid jobId,
            string? message = null,
            CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobCancelPath,
                new ForeignModuleJobCancelRequest
                {
                    JobId = jobId,
                    Message = message,
                },
                ct);

        public Task CancelStaleJobsByActionPrefixAsync(string actionKeyPrefix, CancellationToken ct = default) =>
            client.PostAckAsync(
                ForeignModuleHostCapabilityProtocol.JobCancelStaleByActionPrefixPath,
                new ForeignModuleJobActionPrefixRequest { ActionKeyPrefix = actionKeyPrefix },
                ct);
    }

    private sealed class AgentJobReaderProxy(DotNetSidecarHostCapabilityClient client) : IAgentJobReader
    {
        public async Task<AgentJobResponse?> GetJobAsync(
            Guid jobId,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleTaskIdRequest, ForeignModuleJobGetResponse>(
                ForeignModuleHostCapabilityProtocol.JobGetPath,
                new ForeignModuleTaskIdRequest { Id = jobId },
                ct)).Job;

        public async Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
            string actionKeyPrefix,
            Guid? resourceId = null,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleJobActionPrefixRequest, ForeignModuleJobListResponse>(
                ForeignModuleHostCapabilityProtocol.JobListByActionPrefixPath,
                new ForeignModuleJobActionPrefixRequest
                {
                    ActionKeyPrefix = actionKeyPrefix,
                    ResourceId = resourceId,
                },
                ct)).Jobs;

        public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
            string actionKeyPrefix,
            Guid? resourceId = null,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleJobActionPrefixRequest, ForeignModuleJobSummaryListResponse>(
                ForeignModuleHostCapabilityProtocol.JobListSummariesByActionPrefixPath,
                new ForeignModuleJobActionPrefixRequest
                {
                    ActionKeyPrefix = actionKeyPrefix,
                    ResourceId = resourceId,
                },
                ct)).Jobs;

        public async Task<bool> JobExistsWithActionPrefixAsync(
            Guid jobId,
            string actionKeyPrefix,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleJobExistsWithActionPrefixRequest, ForeignModuleBooleanResponse>(
                ForeignModuleHostCapabilityProtocol.JobExistsWithActionPrefixPath,
                new ForeignModuleJobExistsWithActionPrefixRequest
                {
                    JobId = jobId,
                    ActionKeyPrefix = actionKeyPrefix,
                },
                ct)).Value;
    }

    private sealed class ProtocolContractInvokerProxy(
        DotNetSidecarHostCapabilityClient client,
        ForeignModuleProtocolContractExport export) : IForeignModuleProtocolContractInvoker
    {
        public string ContractName => export.ContractName;
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations => export.Operations;

        public async Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            (await client.PostAsync<ForeignModuleProtocolContractInvokeRequest, ForeignModuleProtocolContractInvokeResponse>(
                ForeignModuleHostCapabilityProtocol.ProtocolContractInvokePath,
                new ForeignModuleProtocolContractInvokeRequest
                {
                    ContractName = ContractName,
                    Operation = operation,
                    Parameters = parameters,
                },
                ct)).Result;
    }

    private sealed class RemoteToolModule(DotNetSidecarHostCapabilityClient client) : ISharpClawCoreModule
    {
        public string Id => "sharpclaw_host_tools";
        public string DisplayName => "SharpClaw Host Tools";
        public string ToolPrefix => "host";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            InvokeAsync(toolName, parameters, ct);

        private async Task<string> InvokeAsync(string toolName, JsonElement parameters, CancellationToken ct) =>
            (await client.PostAsync<ForeignModuleToolInvokeRequest, ForeignModuleToolInvokeResponse>(
                ForeignModuleHostCapabilityProtocol.ModuleToolInvokePath,
                new ForeignModuleToolInvokeRequest
                {
                    ToolName = toolName,
                    Parameters = parameters,
                },
                ct)).Result;
    }
}

internal sealed class DotNetSidecarHostCapabilityClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly string _token;

    private DotNetSidecarHostCapabilityClient(Uri address, string token)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _token = token;
    }

    public static DotNetSidecarHostCapabilityClient? TryCreateFromEnvironment()
    {
        var address = Environment.GetEnvironmentVariable(ForeignModuleHostCapabilityProtocol.AddressEnv);
        var token = Environment.GetEnvironmentVariable(ForeignModuleHostCapabilityProtocol.TokenEnv);
        return string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token)
            ? null
            : new DotNetSidecarHostCapabilityClient(new Uri(address), token);
    }

    public async Task PostAckAsync<TRequest>(string path, TRequest request, CancellationToken ct = default) =>
        _ = await PostAsync<TRequest, ForeignModuleCapabilityAck>(path, request, ct);

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.TryAddWithoutValidation(ForeignModuleProtocol.TokenHeaderName, _token);

        using var response = await _httpClient.SendAsync(message, ct);
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SharpClaw host capability call {path} failed with HTTP {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            return Activator.CreateInstance<TResponse>();

        return JsonSerializer.Deserialize<TResponse>(body, JsonOptions)
            ?? throw new JsonException($"Host capability call {path} returned invalid JSON.");
    }
}
