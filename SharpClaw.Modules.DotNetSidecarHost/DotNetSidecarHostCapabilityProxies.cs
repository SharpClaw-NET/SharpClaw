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
        var client = DotNetSidecarHostCapabilityClient.TryCreateFromEnvironment();
        if (client is null)
            return;

        services.TryAddSingleton(client);
        services.TryAddSingleton<IModuleConfigStore, ModuleConfigStoreProxy>();
        services.TryAddSingleton<ITaskAuthoring, TaskAuthoringProxy>();
        services.TryAddSingleton<ITaskInstanceLauncher, TaskInstanceLauncherProxy>();
        services.TryAddSingleton<IHostQueueMetrics, HostQueueMetricsProxy>();
        services.TryAddSingleton<ICoreEntityIdProvider, CoreEntityIdProviderProxy>();
        services.TryAddSingleton<IAgentManager, AgentManagerProxy>();
        services.TryAddSingleton<IModuleInfoProvider, ModuleInfoProviderProxy>();
        services.TryAddSingleton<IModuleLifecycleManager, ModuleLifecycleManagerProxy>();
        services.TryAddSingleton<IForeignModuleProtocolContractResolver, ProtocolContractResolverProxy>();
        services.TryAddSingleton<IModuleStorageGateway, ModuleStorageGatewayProxy>();
        services.TryAddSingleton<IAgentJobController, AgentJobControllerProxy>();
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

        public (ISharpClawModule Module, string ToolName)? FindToolByName(string toolName) =>
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
            throw new NotSupportedException("Cancelling stale jobs by prefix from a .NET sidecar is not exposed yet.");
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

    private sealed class RemoteToolModule(DotNetSidecarHostCapabilityClient client) : ISharpClawModule
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
