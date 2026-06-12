using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Core.Modules.Foreign;

internal sealed class ForeignModuleHostCapabilityServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _moduleId;
    private readonly IServiceProvider _services;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    private ForeignModuleHostCapabilityServer(
        string moduleId,
        IServiceProvider services,
        TcpListener listener,
        Uri address,
        string token)
    {
        _moduleId = moduleId;
        _services = services;
        _listener = listener;
        Address = address;
        Token = token;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri Address { get; }
    public string Token { get; }

    public static ForeignModuleHostCapabilityServer Start(
        string moduleId,
        IServiceProvider services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(services);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var address = new Uri($"http://127.0.0.1:{endpoint.Port}");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new ForeignModuleHostCapabilityServer(
            moduleId,
            services,
            listener,
            address,
            token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
            return;

        await _stopping.CancelAsync();
        _listener.Stop();

        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (_stopping.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, _stopping.Token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        using (client)
        {
            try
            {
                var request = await ReadRequestAsync(stream, ct);
                if (!string.Equals(
                        request.Headers.GetValueOrDefault(ForeignModuleProtocol.TokenHeaderName),
                        Token,
                        StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, HttpStatusCode.Unauthorized, new { error = "Unauthorized" }, ct);
                    return;
                }

                var response = await HandleRequestAsync(request, ct);
                await WriteJsonAsync(stream, HttpStatusCode.OK, response, ct);
            }
            catch (NotSupportedException ex)
            {
                await WriteJsonAsync(stream, HttpStatusCode.NotImplemented, new { error = ex.Message }, ct);
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = ex.Message }, ct);
            }
            catch (ArgumentException ex)
            {
                await WriteJsonAsync(stream, HttpStatusCode.BadRequest, new { error = ex.Message }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, HttpStatusCode.InternalServerError, new { error = ex.Message }, ct);
            }
        }
    }

    private async Task<object> HandleRequestAsync(CapabilityHttpRequest request, CancellationToken ct)
    {
        var telemetry = _services.GetService<IModuleCapabilityTelemetry>();
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await DispatchRequestAsync(request, ct);
            telemetry?.Record(new ModuleCapabilityTelemetryEvent(
                _moduleId,
                request.Path,
                Success: true,
                Stopwatch.GetElapsedTime(started)));
            return response;
        }
        catch
        {
            telemetry?.Record(new ModuleCapabilityTelemetryEvent(
                _moduleId,
                request.Path,
                Success: false,
                Stopwatch.GetElapsedTime(started)));
            throw;
        }
    }

    private async Task<object> DispatchRequestAsync(CapabilityHttpRequest request, CancellationToken ct)
    {
        using var scope = _services.GetService<IServiceScopeFactory>()?.CreateScope();
        var services = scope?.ServiceProvider ?? _services;

        return request.Path switch
        {
            ForeignModuleHostCapabilityProtocol.ConfigGetPath =>
                await GetConfigAsync(services, Deserialize<ForeignModuleConfigGetRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ConfigSetPath =>
                await SetConfigAsync(services, Deserialize<ForeignModuleConfigSetRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ConfigAllPath =>
                await GetAllConfigAsync(services, ct),
            ForeignModuleHostCapabilityProtocol.LogPath =>
                LogHostMessage(services, Deserialize<ForeignModuleLogRequest>(request)),
            ForeignModuleHostCapabilityProtocol.JobLogPath =>
                await AddJobLogAsync(services, Deserialize<ForeignModuleJobLogRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.JobCompletePath =>
                await CompleteJobAsync(services, Deserialize<ForeignModuleJobCompleteRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.JobFailPath =>
                await FailJobAsync(services, Deserialize<ForeignModuleJobFailRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.JobCancelPath =>
                await CancelJobAsync(services, Deserialize<ForeignModuleJobCancelRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.JobCancelStaleByActionPrefixPath =>
                await CancelStaleJobsByActionPrefixAsync(
                    services,
                    Deserialize<ForeignModuleJobActionPrefixRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.JobGetPath =>
                await GetJobAsync(services, Deserialize<ForeignModuleTaskIdRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.JobListByActionPrefixPath =>
                await ListJobsByActionPrefixAsync(
                    services,
                    Deserialize<ForeignModuleJobActionPrefixRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.JobListSummariesByActionPrefixPath =>
                await ListJobSummariesByActionPrefixAsync(
                    services,
                    Deserialize<ForeignModuleJobActionPrefixRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.JobExistsWithActionPrefixPath =>
                await JobExistsWithActionPrefixAsync(
                    services,
                    Deserialize<ForeignModuleJobExistsWithActionPrefixRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.ProtocolContractsListPath =>
                ListProtocolContracts(services),
            ForeignModuleHostCapabilityProtocol.ProtocolContractInvokePath =>
                await InvokeProtocolContractAsync(
                    services,
                    Deserialize<ForeignModuleProtocolContractInvokeRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.TaskValidatePath =>
                ValidateTask(services, Deserialize<ForeignModuleTaskSourceRequest>(request)),
            ForeignModuleHostCapabilityProtocol.TaskCreatePath =>
                await CreateTaskAsync(services, Deserialize<ForeignModuleTaskSourceRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.TaskGetPath =>
                await GetTaskAsync(services, Deserialize<ForeignModuleTaskIdRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.TaskListPath =>
                await ListTasksAsync(services, ct),
            ForeignModuleHostCapabilityProtocol.TaskUpdatePath =>
                await UpdateTaskAsync(services, Deserialize<ForeignModuleTaskUpdateRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.TaskDeletePath =>
                await DeleteTaskAsync(services, Deserialize<ForeignModuleTaskIdRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.TaskLaunchPath =>
                await LaunchTaskAsync(services, Deserialize<ForeignModuleTaskLaunchRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.TaskContextExecuteStepsPath =>
                await ExecuteTaskContextStepsAsync(
                    services,
                    Deserialize<ForeignModuleTaskContextExecuteStepsRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.TaskContextExecuteEventHandlerPath =>
                await ExecuteTaskContextEventHandlerAsync(
                    services,
                    Deserialize<ForeignModuleTaskContextExecuteEventHandlerRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.CoreAgentIdsPath =>
                new ForeignModuleIdsResponse(await ResolveCoreEntityIds(services).GetAgentIdsAsync(ct)),
            ForeignModuleHostCapabilityProtocol.CoreChannelIdsPath =>
                new ForeignModuleIdsResponse(await ResolveCoreEntityIds(services).GetChannelIdsAsync(ct)),
            ForeignModuleHostCapabilityProtocol.CoreAgentLookupPath =>
                new ForeignModuleLookupItemsResponse([.. (await ResolveCoreEntityIds(services).GetAgentLookupItemsAsync(ct))
                    .Select(item => new ForeignModuleLookupItem(item.Id, item.Name))]),
            ForeignModuleHostCapabilityProtocol.CoreChannelLookupPath =>
                new ForeignModuleLookupItemsResponse([.. (await ResolveCoreEntityIds(services).GetChannelLookupItemsAsync(ct))
                    .Select(item => new ForeignModuleLookupItem(item.Id, item.Name))]),
            ForeignModuleHostCapabilityProtocol.ContextAccessibleThreadsPath =>
                await GetAccessibleContextThreadsAsync(
                    services,
                    Deserialize<ForeignModuleContextAccessibleThreadsRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.ContextThreadMessagesPath =>
                await GetContextThreadMessagesAsync(
                    services,
                    Deserialize<ForeignModuleContextThreadMessagesRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.ConversationSteerPath =>
                await AddConversationSteeringAsync(
                    services,
                    Deserialize<ConversationSteeringRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.ConversationSteeringListPath =>
                await ListConversationSteeringAsync(
                    services,
                    Deserialize<ForeignModuleConversationSteeringListRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.QueueMetricsPath =>
                await GetQueueMetricsAsync(services, ct),
            ForeignModuleHostCapabilityProtocol.HostAgentChatPath =>
                await HostAgentChatAsync(services, Deserialize<ForeignModuleHostAgentChatRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.HostAgentChatStreamPath =>
                await HostAgentChatStreamAsync(services, Deserialize<ForeignModuleHostAgentChatRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.HostAgentChatToThreadPath =>
                await HostAgentChatToThreadAsync(
                    services,
                    Deserialize<ForeignModuleHostAgentChatToThreadRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.HostAgentParseStructuredResponsePath =>
                HostAgentParseStructuredResponse(
                    services,
                    Deserialize<ForeignModuleHostAgentParseStructuredResponseRequest>(request)),
            ForeignModuleHostCapabilityProtocol.HostAgentFindModelPath =>
                new ForeignModuleHostAgentIdResponse(
                    await ResolveHostAgentBridge(services).FindModelAsync(
                        Deserialize<ForeignModuleHostAgentFindRequest>(request).Search,
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentFindProviderPath =>
                new ForeignModuleHostAgentIdResponse(
                    await ResolveHostAgentBridge(services).FindProviderAsync(
                        Deserialize<ForeignModuleHostAgentFindRequest>(request).Search,
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentFindAgentPath =>
                new ForeignModuleHostAgentIdResponse(
                    await ResolveHostAgentBridge(services).FindAgentAsync(
                        Deserialize<ForeignModuleHostAgentFindRequest>(request).Search,
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentFindRolePath =>
                new ForeignModuleHostAgentIdResponse(
                    await ResolveHostAgentBridge(services).FindRoleAsync(
                        Deserialize<ForeignModuleHostAgentFindRequest>(request).Search,
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentFindChannelPath =>
                new ForeignModuleHostAgentIdResponse(
                    await ResolveHostAgentBridge(services).FindChannelAsync(
                        Deserialize<ForeignModuleHostAgentFindRequest>(request).Search,
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentCreateAgentPath =>
                new ForeignModuleHostAgentIdResponse(
                    await HostAgentCreateAgentAsync(
                        services,
                        Deserialize<ForeignModuleHostAgentCreateAgentRequest>(request),
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentCreateThreadPath =>
                new ForeignModuleHostAgentIdResponse(
                    await HostAgentCreateThreadAsync(
                        services,
                        Deserialize<ForeignModuleHostAgentCreateThreadRequest>(request),
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentCreateRolePath =>
                new ForeignModuleHostAgentIdResponse(
                    await HostAgentCreateRoleAsync(
                        services,
                        Deserialize<ForeignModuleHostAgentCreateRoleRequest>(request),
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentSetRolePermissionsPath =>
                await HostAgentSetRolePermissionsAsync(
                    services,
                    Deserialize<ForeignModuleHostAgentSetRolePermissionsRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.HostAgentAssignRolePath =>
                await HostAgentAssignRoleAsync(
                    services,
                    Deserialize<ForeignModuleHostAgentAssignRoleRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.HostAgentCreateChannelPath =>
                new ForeignModuleHostAgentIdResponse(
                    await HostAgentCreateChannelAsync(
                        services,
                        Deserialize<ForeignModuleHostAgentCreateChannelRequest>(request),
                        ct)),
            ForeignModuleHostCapabilityProtocol.HostAgentAddAllowedAgentPath =>
                await HostAgentAddAllowedAgentAsync(
                    services,
                    Deserialize<ForeignModuleHostAgentAddAllowedAgentRequest>(request),
                    ct),
            ForeignModuleHostCapabilityProtocol.AgentCreateSubAgentPath =>
                await CreateSubAgentAsync(services, Deserialize<ForeignModuleAgentCreateRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.AgentUpdatePath =>
                await UpdateAgentAsync(services, Deserialize<ForeignModuleAgentUpdateRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.AgentSetHeaderPath =>
                await SetAgentHeaderAsync(services, Deserialize<ForeignModuleSetHeaderRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ChannelSetHeaderPath =>
                await SetChannelHeaderAsync(services, Deserialize<ForeignModuleSetHeaderRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelEnsureProviderPath =>
                await EnsureProviderAsync(services, Deserialize<ForeignModuleModelEnsureProviderRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelEnsureModelPath =>
                await EnsureModelAsync(services, Deserialize<ForeignModuleModelEnsureModelRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelProviderInfoPath =>
                await GetModelProviderInfoAsync(services, Deserialize<ForeignModuleModelMetadataRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelLocalFilePathPath =>
                await GetLocalModelFilePathAsync(services, Deserialize<ForeignModuleModelMetadataRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelMetadataPath =>
                await GetModelMetadataAsync(services, Deserialize<ForeignModuleModelMetadataRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModelDeletePath =>
                await DeleteModelAsync(services, Deserialize<ForeignModuleModelDeleteRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModulesExternalRootPath =>
                new ForeignModuleExternalModulesRootResponse(ResolveModuleLifecycle(services).ExternalModulesDir),
            ForeignModuleHostCapabilityProtocol.ModulesInfoListPath =>
                new ForeignModuleInfoListResponse(ResolveModuleInfo(services).GetAllModules()),
            ForeignModuleHostCapabilityProtocol.ModuleRegisteredPath =>
                IsModuleRegistered(services, Deserialize<ForeignModuleRegisteredRequest>(request)),
            ForeignModuleHostCapabilityProtocol.ModuleToolPrefixRegisteredPath =>
                IsToolPrefixRegistered(services, Deserialize<ForeignModuleToolPrefixRegisteredRequest>(request)),
            ForeignModuleHostCapabilityProtocol.ModuleLoadPath =>
                await LoadModuleAsync(services, Deserialize<ForeignModuleLoadRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModuleUnloadPath =>
                await UnloadModuleAsync(services, Deserialize<ForeignModuleModuleIdRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModuleReloadPath =>
                await ReloadModuleAsync(services, Deserialize<ForeignModuleModuleIdRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModuleToolInvokePath =>
                await InvokeModuleToolAsync(services, Deserialize<ForeignModuleToolInvokeRequest>(request), ct),
            ForeignModuleHostCapabilityProtocol.ModuleStorageListPath =>
                new ForeignModuleStorageContractsResponse(ResolveModuleStorage(services).ListContracts()),
            ForeignModuleHostCapabilityProtocol.ModuleStorageInvokePath =>
                await InvokeModuleStorageAsync(services, Deserialize<ForeignModuleStorageInvokeRequest>(request), ct),
            _ => throw new ArgumentException($"Unknown SharpClaw host capability path '{request.Path}'."),
        };
    }

    private async Task<ForeignModuleConfigGetResponse> GetConfigAsync(
        IServiceProvider services,
        ForeignModuleConfigGetRequest request,
        CancellationToken ct)
    {
        RequireKey(request.Key);
        var store = ResolveConfigStore(services);
        return new ForeignModuleConfigGetResponse(await store.GetAsync(request.Key, ct));
    }

    private async Task<ForeignModuleCapabilityAck> SetConfigAsync(
        IServiceProvider services,
        ForeignModuleConfigSetRequest request,
        CancellationToken ct)
    {
        RequireKey(request.Key);
        var store = ResolveConfigStore(services);
        await store.SetAsync(request.Key, request.Value, ct);
        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleConfigAllResponse> GetAllConfigAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var store = ResolveConfigStore(services);
        return new ForeignModuleConfigAllResponse(await store.GetAllAsync(ct));
    }

    private ForeignModuleCapabilityAck LogHostMessage(
        IServiceProvider services,
        ForeignModuleLogRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Log message is required.", nameof(request));

        var loggerFactory = services.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger($"SharpClaw.Modules.Foreign.{_moduleId}");
        logger?.Log(
            ParseLogLevel(request.Level),
            "Foreign module {ModuleId}: {Message}",
            _moduleId,
            request.Message);

        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleCapabilityAck> AddJobLogAsync(
        IServiceProvider services,
        ForeignModuleJobLogRequest request,
        CancellationToken ct)
    {
        RequireJobId(request.JobId);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Job log message is required.", nameof(request));

        await ResolveJobController(services)
            .AddJobLogAsync(request.JobId, request.Message, request.Level, ct);
        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleCapabilityAck> CompleteJobAsync(
        IServiceProvider services,
        ForeignModuleJobCompleteRequest request,
        CancellationToken ct)
    {
        RequireJobId(request.JobId);
        await ResolveJobController(services)
            .MarkJobCompletedAsync(request.JobId, request.ResultData, request.Message, ct);
        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleCapabilityAck> FailJobAsync(
        IServiceProvider services,
        ForeignModuleJobFailRequest request,
        CancellationToken ct)
    {
        RequireJobId(request.JobId);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Job failure message is required.", nameof(request));

        await ResolveJobController(services)
            .MarkJobFailedAsync(request.JobId, request.Message, request.Details, ct);
        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleCapabilityAck> CancelJobAsync(
        IServiceProvider services,
        ForeignModuleJobCancelRequest request,
        CancellationToken ct)
    {
        RequireJobId(request.JobId);
        await ResolveJobController(services)
            .MarkJobCancelledAsync(request.JobId, request.Message, ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleCapabilityAck> CancelStaleJobsByActionPrefixAsync(
        IServiceProvider services,
        ForeignModuleJobActionPrefixRequest request,
        CancellationToken ct)
    {
        RequireActionKeyPrefix(request.ActionKeyPrefix);
        await ResolveJobController(services)
            .CancelStaleJobsByActionPrefixAsync(request.ActionKeyPrefix, ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleJobGetResponse> GetJobAsync(
        IServiceProvider services,
        ForeignModuleTaskIdRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Job ID is required.");
        return new ForeignModuleJobGetResponse(
            await ResolveJobReader(services).GetJobAsync(request.Id, ct));
    }

    private static async Task<ForeignModuleJobListResponse> ListJobsByActionPrefixAsync(
        IServiceProvider services,
        ForeignModuleJobActionPrefixRequest request,
        CancellationToken ct)
    {
        RequireActionKeyPrefix(request.ActionKeyPrefix);
        return new ForeignModuleJobListResponse(
            await ResolveJobReader(services)
                .ListJobsByActionPrefixAsync(request.ActionKeyPrefix, request.ResourceId, ct));
    }

    private static async Task<ForeignModuleJobSummaryListResponse> ListJobSummariesByActionPrefixAsync(
        IServiceProvider services,
        ForeignModuleJobActionPrefixRequest request,
        CancellationToken ct)
    {
        RequireActionKeyPrefix(request.ActionKeyPrefix);
        return new ForeignModuleJobSummaryListResponse(
            await ResolveJobReader(services)
                .ListJobSummariesByActionPrefixAsync(request.ActionKeyPrefix, request.ResourceId, ct));
    }

    private static async Task<ForeignModuleBooleanResponse> JobExistsWithActionPrefixAsync(
        IServiceProvider services,
        ForeignModuleJobExistsWithActionPrefixRequest request,
        CancellationToken ct)
    {
        RequireId(request.JobId, "Job ID is required.");
        RequireActionKeyPrefix(request.ActionKeyPrefix);
        return new ForeignModuleBooleanResponse(
            await ResolveJobReader(services)
                .JobExistsWithActionPrefixAsync(request.JobId, request.ActionKeyPrefix, ct));
    }

    private static ForeignModuleProtocolContractsListResponse ListProtocolContracts(
        IServiceProvider services) =>
        new(ResolveProtocolContractResolver(services).GetAllExports());

    private static async Task<ForeignModuleProtocolContractInvokeResponse> InvokeProtocolContractAsync(
        IServiceProvider services,
        ForeignModuleProtocolContractInvokeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ContractName))
            throw new ArgumentException("Protocol contract name is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Operation))
            throw new ArgumentException("Protocol contract operation is required.", nameof(request));

        var invoker = ResolveProtocolContractResolver(services).Resolve(request.ContractName)
            ?? throw new NotSupportedException(
                $"The SharpClaw host does not provide protocol contract '{request.ContractName}'.");
        return new ForeignModuleProtocolContractInvokeResponse(
            await invoker.InvokeAsync(request.Operation, request.Parameters, ct));
    }

    private static TaskValidationResponse ValidateTask(
        IServiceProvider services,
        ForeignModuleTaskSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Task source text is required.", nameof(request));

        return ResolveTaskAuthoring(services).ValidateDefinition(request.SourceText);
    }

    private static async Task<TaskDefinitionResponse> CreateTaskAsync(
        IServiceProvider services,
        ForeignModuleTaskSourceRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Task source text is required.", nameof(request));

        return await ResolveTaskAuthoring(services)
            .CreateDefinitionAsync(new CreateTaskDefinitionRequest(request.SourceText), ct);
    }

    private static async Task<ForeignModuleTaskGetResponse> GetTaskAsync(
        IServiceProvider services,
        ForeignModuleTaskIdRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Task definition ID is required.");
        return new ForeignModuleTaskGetResponse(
            await ResolveTaskAuthoring(services).GetDefinitionAsync(request.Id, ct));
    }

    private static async Task<ForeignModuleTaskListResponse> ListTasksAsync(
        IServiceProvider services,
        CancellationToken ct) =>
        new(await ResolveTaskAuthoring(services).ListDefinitionsAsync(ct));

    private static async Task<ForeignModuleTaskGetResponse> UpdateTaskAsync(
        IServiceProvider services,
        ForeignModuleTaskUpdateRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Task definition ID is required.");
        var updated = await ResolveTaskAuthoring(services)
            .UpdateDefinitionAsync(
                request.Id,
                new UpdateTaskDefinitionRequest(request.SourceText, request.IsActive),
                ct);
        return new ForeignModuleTaskGetResponse(updated);
    }

    private static async Task<ForeignModuleTaskDeleteResponse> DeleteTaskAsync(
        IServiceProvider services,
        ForeignModuleTaskIdRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Task definition ID is required.");
        return new ForeignModuleTaskDeleteResponse(
            await ResolveTaskAuthoring(services).DeleteDefinitionAsync(request.Id, ct));
    }

    private static async Task<ForeignModuleTaskLaunchResponse> LaunchTaskAsync(
        IServiceProvider services,
        ForeignModuleTaskLaunchRequest request,
        CancellationToken ct)
    {
        RequireId(request.TaskDefinitionId, "Task definition ID is required.");
        var instanceId = await ResolveTaskLauncher(services)
            .LaunchAsync(
                request.TaskDefinitionId,
                request.ParameterValues,
                request.CallerAgentId,
                request.ChannelId,
                request.ContextId,
                ct);
        return new ForeignModuleTaskLaunchResponse(instanceId);
    }

    private static async Task<ForeignModuleTaskContextExecutionResponse> ExecuteTaskContextStepsAsync(
        IServiceProvider services,
        ForeignModuleTaskContextExecuteStepsRequest request,
        CancellationToken ct)
    {
        var context = ResolveTaskContext(services, request.ContextId);
        ApplyTaskContextSnapshot(context, request.ChannelId, request.Variables);
        var result = await context.ExecuteStepsAsync(
            [.. request.Steps.Select(ForeignModuleProxy.ToTaskStepDefinition)],
            ct);
        return CreateTaskContextResponse(result, context);
    }

    private static async Task<ForeignModuleTaskContextExecutionResponse> ExecuteTaskContextEventHandlerAsync(
        IServiceProvider services,
        ForeignModuleTaskContextExecuteEventHandlerRequest request,
        CancellationToken ct)
    {
        var registry = ResolveTaskContextRegistry(services);
        if (!registry.TryGetEventHandler(request.HandlerId, out var context, out var handler))
        {
            throw new NotSupportedException(
                $"Task event handler callback '{request.HandlerId}' is not active.");
        }

        ApplyTaskContextSnapshot(context, request.ChannelId, request.Variables);
        await handler.ExecuteBodyAsync(ct);
        return CreateTaskContextResponse(TaskStepResult.Continue, context);
    }

    private static ITaskStepExecutionContext ResolveTaskContext(
        IServiceProvider services,
        string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
            throw new ArgumentException("Task context callback ID is required.", nameof(contextId));

        var registry = ResolveTaskContextRegistry(services);
        return registry.TryGetContext(contextId, out var context)
            ? context
            : throw new NotSupportedException($"Task context callback '{contextId}' is not active.");
    }

    private static void ApplyTaskContextSnapshot(
        ITaskStepExecutionContext context,
        Guid? channelId,
        IReadOnlyDictionary<string, JsonElement>? variables)
    {
        if (channelId is { } targetChannelId)
            context.SetChannelId(targetChannelId);

        if (variables is null)
            return;

        foreach (var (key, value) in variables)
            context.Variables[key] = ConvertJsonValue(value);
    }

    private static ForeignModuleTaskContextExecutionResponse CreateTaskContextResponse(
        TaskStepResult result,
        ITaskStepExecutionContext context) =>
        new(
            result,
            context.ChannelId,
            context.Variables.ToDictionary(
                pair => pair.Key,
                pair => SerializeVariableValue(pair.Value),
                StringComparer.Ordinal));

    private static object? ConvertJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.Clone(),
        };

    private static JsonElement SerializeVariableValue(object? value)
    {
        if (value is JsonElement element)
            return element.Clone();

        try
        {
            return value is null
                ? JsonSerializer.SerializeToElement((string?)null, JsonOptions)
                : JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);
        }
        catch (NotSupportedException)
        {
            return JsonSerializer.SerializeToElement(value?.ToString(), JsonOptions);
        }
    }

    private static async Task<ForeignModuleContextThreadsResponse> GetAccessibleContextThreadsAsync(
        IServiceProvider services,
        ForeignModuleContextAccessibleThreadsRequest request,
        CancellationToken ct)
    {
        RequireId(request.AgentId, "Agent ID is required.");
        RequireId(request.CurrentChannelId, "Current channel ID is required.");
        if (string.IsNullOrWhiteSpace(request.CrossThreadPermissionKey))
            throw new ArgumentException("Cross-thread permission key is required.", nameof(request));

        return new ForeignModuleContextThreadsResponse(
            await ResolveHostContextDataReader(services).GetAccessibleThreadsAsync(
                request.AgentId,
                request.CurrentChannelId,
                request.CrossThreadPermissionKey,
                ct));
    }

    private static async Task<ForeignModuleContextMessagesResponse> GetContextThreadMessagesAsync(
        IServiceProvider services,
        ForeignModuleContextThreadMessagesRequest request,
        CancellationToken ct)
    {
        RequireId(request.ThreadId, "Thread ID is required.");
        return new ForeignModuleContextMessagesResponse(
            await ResolveHostContextDataReader(services).GetThreadMessagesAsync(
                request.ThreadId,
                request.MaxMessages,
                ct));
    }

    private static async Task<ForeignModuleConversationSteerResponse> AddConversationSteeringAsync(
        IServiceProvider services,
        ConversationSteeringRequest request,
        CancellationToken ct) =>
        new(await ResolveConversationSteering(services).AddAsync(request, ct));

    private static async Task<ForeignModuleConversationSteeringListResponse> ListConversationSteeringAsync(
        IServiceProvider services,
        ForeignModuleConversationSteeringListRequest request,
        CancellationToken ct)
    {
        RequireId(request.ChannelId, "Channel ID is required.");
        return new ForeignModuleConversationSteeringListResponse(
            await ResolveConversationSteering(services)
                .ListAsync(request.ChannelId, request.ThreadId, request.Limit, ct));
    }

    private static async Task<ForeignModuleQueueMetricsResponse> GetQueueMetricsAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var metrics = ResolveQueueMetrics(services);
        return new ForeignModuleQueueMetricsResponse(
            await metrics.GetPendingJobCountAsync(ct),
            await metrics.GetPendingTaskCountAsync(ct),
            await metrics.GetSchedulerPendingJobCountAsync(ct));
    }

    private static async Task<ForeignModuleHostAgentTextResponse> HostAgentChatAsync(
        IServiceProvider services,
        ForeignModuleHostAgentChatRequest request,
        CancellationToken ct) =>
        new(await ResolveHostAgentBridge(services).ChatAsync(
            request.InstanceId,
            request.TaskName,
            request.Message,
            request.AgentId,
            ct));

    private static async Task<ForeignModuleHostAgentTextResponse> HostAgentChatStreamAsync(
        IServiceProvider services,
        ForeignModuleHostAgentChatRequest request,
        CancellationToken ct) =>
        new(await ResolveHostAgentBridge(services).ChatStreamAsync(
            request.InstanceId,
            request.TaskName,
            request.Message,
            request.AgentId,
            ct));

    private static async Task<ForeignModuleHostAgentTextResponse> HostAgentChatToThreadAsync(
        IServiceProvider services,
        ForeignModuleHostAgentChatToThreadRequest request,
        CancellationToken ct) =>
        new(await ResolveHostAgentBridge(services).ChatToThreadAsync(
            request.InstanceId,
            request.TaskName,
            request.ThreadId,
            request.Message,
            request.AgentId,
            ct));

    private static ForeignModuleHostAgentTextResponse HostAgentParseStructuredResponse(
        IServiceProvider services,
        ForeignModuleHostAgentParseStructuredResponseRequest request) =>
        new(ResolveHostAgentBridge(services).ParseStructuredResponse(
            request.InstanceId,
            request.Text,
            request.TypeName));

    private static Task<Guid> HostAgentCreateAgentAsync(
        IServiceProvider services,
        ForeignModuleHostAgentCreateAgentRequest request,
        CancellationToken ct) =>
        ResolveHostAgentBridge(services).CreateAgentAsync(
            request.InstanceId,
            request.Name,
            request.ModelId,
            request.SystemPrompt,
            request.CustomId,
            ct);

    private static Task<Guid> HostAgentCreateThreadAsync(
        IServiceProvider services,
        ForeignModuleHostAgentCreateThreadRequest request,
        CancellationToken ct) =>
        ResolveHostAgentBridge(services).CreateThreadAsync(
            request.InstanceId,
            request.ChannelId,
            request.ThreadName,
            ct);

    private static Task<Guid> HostAgentCreateRoleAsync(
        IServiceProvider services,
        ForeignModuleHostAgentCreateRoleRequest request,
        CancellationToken ct) =>
        ResolveHostAgentBridge(services).CreateRoleAsync(request.RoleName, ct);

    private static async Task<ForeignModuleCapabilityAck> HostAgentSetRolePermissionsAsync(
        IServiceProvider services,
        ForeignModuleHostAgentSetRolePermissionsRequest request,
        CancellationToken ct)
    {
        await ResolveHostAgentBridge(services).SetRolePermissionsAsync(
            request.RoleId,
            request.RequestJson,
            ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleCapabilityAck> HostAgentAssignRoleAsync(
        IServiceProvider services,
        ForeignModuleHostAgentAssignRoleRequest request,
        CancellationToken ct)
    {
        await ResolveHostAgentBridge(services).AssignRoleAsync(
            request.AgentId,
            request.RoleId,
            ct);
        return new ForeignModuleCapabilityAck();
    }

    private static Task<Guid> HostAgentCreateChannelAsync(
        IServiceProvider services,
        ForeignModuleHostAgentCreateChannelRequest request,
        CancellationToken ct) =>
        ResolveHostAgentBridge(services).CreateChannelAsync(
            request.InstanceId,
            request.Title,
            request.AgentId,
            request.CustomId,
            ct);

    private static async Task<ForeignModuleCapabilityAck> HostAgentAddAllowedAgentAsync(
        IServiceProvider services,
        ForeignModuleHostAgentAddAllowedAgentRequest request,
        CancellationToken ct)
    {
        await ResolveHostAgentBridge(services).AddAllowedAgentAsync(
            request.InstanceId,
            request.AgentId,
            request.ChannelId,
            ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleAgentCreateResponse> CreateSubAgentAsync(
        IServiceProvider services,
        ForeignModuleAgentCreateRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Agent name is required.", nameof(request));
        RequireId(request.ModelId, "Model ID is required.");

        var (agentId, modelName, agentName) = await ResolveAgentManager(services)
            .CreateSubAgentAsync(request.Name, request.ModelId, request.SystemPrompt, ct);
        return new ForeignModuleAgentCreateResponse(agentId, modelName, agentName);
    }

    private static async Task<ForeignModuleAgentUpdateResponse> UpdateAgentAsync(
        IServiceProvider services,
        ForeignModuleAgentUpdateRequest request,
        CancellationToken ct)
    {
        RequireId(request.AgentId, "Agent ID is required.");
        return new ForeignModuleAgentUpdateResponse(
            await ResolveAgentManager(services)
                .UpdateAgentAsync(
                    request.AgentId,
                    request.Name,
                    request.SystemPrompt,
                    request.ModelId,
                    ct));
    }

    private static async Task<ForeignModuleCapabilityAck> SetAgentHeaderAsync(
        IServiceProvider services,
        ForeignModuleSetHeaderRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Agent ID is required.");
        await ResolveAgentManager(services).SetAgentHeaderAsync(request.Id, request.Header, ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleCapabilityAck> SetChannelHeaderAsync(
        IServiceProvider services,
        ForeignModuleSetHeaderRequest request,
        CancellationToken ct)
    {
        RequireId(request.Id, "Channel ID is required.");
        await ResolveAgentManager(services).SetChannelHeaderAsync(request.Id, request.Header, ct);
        return new ForeignModuleCapabilityAck();
    }

    private static async Task<ForeignModuleGuidResponse> EnsureProviderAsync(
        IServiceProvider services,
        ForeignModuleModelEnsureProviderRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderKey))
            throw new ArgumentException("Provider key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("Provider display name is required.", nameof(request));

        return new ForeignModuleGuidResponse(
            await ResolveModelRegistrar(services)
                .EnsureProviderAsync(request.ProviderKey, request.DisplayName, ct));
    }

    private static async Task<ForeignModuleGuidResponse> EnsureModelAsync(
        IServiceProvider services,
        ForeignModuleModelEnsureModelRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName))
            throw new ArgumentException("Model name is required.", nameof(request));
        RequireId(request.ProviderId, "Provider ID is required.");

        return new ForeignModuleGuidResponse(
            await ResolveModelRegistrar(services)
                .EnsureModelAsync(request.ModelName, request.ProviderId, request.CapabilityTags, ct));
    }

    private static async Task<ForeignModuleModelMetadataResponse> GetModelMetadataAsync(
        IServiceProvider services,
        ForeignModuleModelMetadataRequest request,
        CancellationToken ct)
    {
        RequireId(request.ModelId, "Model ID is required.");
        return new ForeignModuleModelMetadataResponse(
            await ResolveModelRegistrar(services).GetModelMetadataAsync(request.ModelId, ct));
    }

    private static async Task<ForeignModuleModelProviderInfoResponse> GetModelProviderInfoAsync(
        IServiceProvider services,
        ForeignModuleModelMetadataRequest request,
        CancellationToken ct)
    {
        RequireId(request.ModelId, "Model ID is required.");
        return new ForeignModuleModelProviderInfoResponse(
            await ResolveModelInfoProvider(services).GetModelProviderInfoAsync(request.ModelId, ct));
    }

    private static async Task<ForeignModuleModelLocalFilePathResponse> GetLocalModelFilePathAsync(
        IServiceProvider services,
        ForeignModuleModelMetadataRequest request,
        CancellationToken ct)
    {
        RequireId(request.ModelId, "Model ID is required.");
        return new ForeignModuleModelLocalFilePathResponse(
            await ResolveModelInfoProvider(services).GetLocalModelFilePathAsync(request.ModelId, ct));
    }

    private static async Task<ForeignModuleBooleanResponse> DeleteModelAsync(
        IServiceProvider services,
        ForeignModuleModelDeleteRequest request,
        CancellationToken ct)
    {
        RequireId(request.ModelId, "Model ID is required.");
        return new ForeignModuleBooleanResponse(
            await ResolveModelRegistrar(services).DeleteModelAsync(request.ModelId, ct));
    }

    private static ForeignModuleRegisteredResponse IsModuleRegistered(
        IServiceProvider services,
        ForeignModuleRegisteredRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleId))
            throw new ArgumentException("Module ID is required.", nameof(request));

        return new ForeignModuleRegisteredResponse(
            ResolveModuleLifecycle(services).IsModuleRegistered(request.ModuleId));
    }

    private static ForeignModuleRegisteredResponse IsToolPrefixRegistered(
        IServiceProvider services,
        ForeignModuleToolPrefixRegisteredRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ToolPrefix))
            throw new ArgumentException("Tool prefix is required.", nameof(request));

        return new ForeignModuleRegisteredResponse(
            ResolveModuleLifecycle(services).IsToolPrefixRegistered(request.ToolPrefix));
    }

    private async Task<ForeignModuleStateResponseEnvelope> LoadModuleAsync(
        IServiceProvider services,
        ForeignModuleLoadRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleDir))
            throw new ArgumentException("Module directory is required.", nameof(request));

        return new ForeignModuleStateResponseEnvelope(
            await ResolveModuleLifecycle(services).LoadExternalAsync(request.ModuleDir, _services, ct));
    }

    private static async Task<ForeignModuleCapabilityAck> UnloadModuleAsync(
        IServiceProvider services,
        ForeignModuleModuleIdRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleId))
            throw new ArgumentException("Module ID is required.", nameof(request));

        await ResolveModuleLifecycle(services).UnloadExternalAsync(request.ModuleId, ct);
        return new ForeignModuleCapabilityAck();
    }

    private async Task<ForeignModuleStateResponseEnvelope> ReloadModuleAsync(
        IServiceProvider services,
        ForeignModuleModuleIdRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleId))
            throw new ArgumentException("Module ID is required.", nameof(request));

        return new ForeignModuleStateResponseEnvelope(
            await ResolveModuleLifecycle(services).ReloadExternalAsync(request.ModuleId, _services, ct));
    }

    private static async Task<ForeignModuleToolInvokeResponse> InvokeModuleToolAsync(
        IServiceProvider services,
        ForeignModuleToolInvokeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName))
            throw new ArgumentException("Tool name is required.", nameof(request));

        var lifecycle = ResolveModuleLifecycle(services);
        var toolEntry = lifecycle.FindToolByName(request.ToolName)
            ?? throw new NotSupportedException($"Tool '{request.ToolName}' was not found in any loaded module.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutSeconds is > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds.Value));

        using var emptyParameters = request.Parameters.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}")
            : null;
        var parameters = emptyParameters?.RootElement ?? request.Parameters;
        var job = new AgentJobContext(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            ResourceId: null,
            ActionKey: request.ToolName);
        var result = await toolEntry.Module.ExecuteToolAsync(
            toolEntry.ToolName,
            parameters,
            job,
            services,
            timeout.Token);

        return new ForeignModuleToolInvokeResponse(result);
    }

    private async Task<ForeignModuleStorageInvokeResponse> InvokeModuleStorageAsync(
        IServiceProvider services,
        ForeignModuleStorageInvokeRequest request,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ModuleId)
            && !string.Equals(request.ModuleId, _moduleId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Module storage access is restricted to module '{_moduleId}'.",
                nameof(request));
        }

        var moduleId = _moduleId;
        if (string.IsNullOrWhiteSpace(request.StorageName))
            throw new ArgumentException("Storage name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Operation))
            throw new ArgumentException("Storage operation is required.", nameof(request));

        using var emptyParameters = request.Parameters.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}")
            : null;
        var parameters = emptyParameters?.RootElement ?? request.Parameters;
        return new ForeignModuleStorageInvokeResponse(
            await ResolveModuleStorage(services).InvokeAsync(
                moduleId,
                request.StorageName,
                request.Operation,
                parameters,
                ct));
    }

    private IModuleConfigStore ResolveConfigStore(IServiceProvider services)
    {
        if (services.GetService<IModuleConfigStore>() is { } store)
            return store;

        if (services.GetService<SharpClawDbContext>() is { } db)
            return new ModuleConfigStore(db, _moduleId);

        throw new NotSupportedException("The SharpClaw host did not provide a module config store.");
    }

    private static IAgentJobController ResolveJobController(IServiceProvider services) =>
        services.GetService<IAgentJobController>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide a job controller.");

    private static IAgentJobReader ResolveJobReader(IServiceProvider services) =>
        services.GetService<IAgentJobReader>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide a job reader.");

    private static IForeignModuleProtocolContractResolver ResolveProtocolContractResolver(IServiceProvider services) =>
        services.GetService<IForeignModuleProtocolContractResolver>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide a protocol contract resolver.");

    private static ITaskAuthoring ResolveTaskAuthoring(IServiceProvider services) =>
        services.GetService<ITaskAuthoring>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide task authoring.");

    private static ITaskInstanceLauncher ResolveTaskLauncher(IServiceProvider services) =>
        services.GetService<ITaskInstanceLauncher>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide a task launcher.");

    private static ICoreEntityIdProvider ResolveCoreEntityIds(IServiceProvider services) =>
        services.GetService<ICoreEntityIdProvider>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide core entity lookup.");

    private static IHostContextDataReader ResolveHostContextDataReader(IServiceProvider services) =>
        services.GetService<IHostContextDataReader>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide context data reading.");

    private static IConversationSteering ResolveConversationSteering(IServiceProvider services) =>
        services.GetService<IConversationSteering>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide conversation steering.");

    private static IHostQueueMetrics ResolveQueueMetrics(IServiceProvider services) =>
        services.GetService<IHostQueueMetrics>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide queue metrics.");

    private static IHostAgentBridge ResolveHostAgentBridge(IServiceProvider services) =>
        services.GetService<IHostAgentBridge>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide an agent bridge.");

    private static ForeignModuleTaskContextRegistry ResolveTaskContextRegistry(IServiceProvider services) =>
        services.GetService<ForeignModuleTaskContextRegistry>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide task context callbacks.");

    private static IAgentManager ResolveAgentManager(IServiceProvider services) =>
        services.GetService<IAgentManager>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide agent management.");

    private static IModelRegistrar ResolveModelRegistrar(IServiceProvider services) =>
        services.GetService<IModelRegistrar>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide model registration.");

    private static IModelInfoProvider ResolveModelInfoProvider(IServiceProvider services) =>
        services.GetService<IModelInfoProvider>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide model information.");

    private static IModuleLifecycleManager ResolveModuleLifecycle(IServiceProvider services) =>
        services.GetService<IModuleLifecycleManager>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide module lifecycle management.");

    private static IModuleInfoProvider ResolveModuleInfo(IServiceProvider services) =>
        services.GetService<IModuleInfoProvider>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide module information.");

    private static IModuleStorageGateway ResolveModuleStorage(IServiceProvider services) =>
        services.GetService<IModuleStorageGateway>()
        ?? throw new NotSupportedException("The SharpClaw host did not provide module storage capabilities.");

    private static T Deserialize<T>(CapabilityHttpRequest request)
    {
        if (request.Body.Length == 0)
            return Activator.CreateInstance<T>();

        return JsonSerializer.Deserialize<T>(request.Body, JsonOptions)
            ?? throw new JsonException("Request body was empty.");
    }

    private static void RequireKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Config key is required.", nameof(key));
    }

    private static void RequireJobId(Guid jobId)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job ID is required.", nameof(jobId));
    }

    private static void RequireId(Guid id, string message)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(message, nameof(id));
    }

    private static void RequireActionKeyPrefix(string actionKeyPrefix)
    {
        if (string.IsNullOrWhiteSpace(actionKeyPrefix))
            throw new ArgumentException("Action key prefix is required.", nameof(actionKeyPrefix));
    }

    private static LogLevel ParseLogLevel(string? level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

    private static async Task<CapabilityHttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var received = new MemoryStream();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break;

                received.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            if (headerEnd < 0)
                throw new ArgumentException("Invalid HTTP request.");

            var bytes = received.ToArray();
            var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var headerLength = headerEnd + 4;
            var bodyPrefixLength = Math.Max(0, bytes.Length - headerLength);
            var (method, path, headers) = ParseHeaders(headerText);
            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var lengthText))
                int.TryParse(lengthText, out contentLength);

            if (contentLength == 0
                && headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
                && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = new byte[bodyPrefixLength];
                if (bodyPrefixLength > 0)
                    Array.Copy(bytes, headerLength, prefix, 0, bodyPrefixLength);

                return new CapabilityHttpRequest(
                    method,
                    path,
                    headers,
                    await ReadChunkedBodyAsync(prefix, stream, ct));
            }

            var body = new byte[contentLength];
            if (bodyPrefixLength > 0 && contentLength > 0)
            {
                var copyLength = Math.Min(bodyPrefixLength, contentLength);
                Array.Copy(bytes, headerLength, body, 0, copyLength);
            }

            var offset = Math.Min(bodyPrefixLength, contentLength);
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct);
                if (read == 0)
                    break;

                offset += read;
            }

            return new CapabilityHttpRequest(method, path, headers, body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        byte[] prefix,
        NetworkStream stream,
        CancellationToken ct)
    {
        var reader = new PrefixNetworkReader(prefix, stream);
        using var body = new MemoryStream();

        while (true)
        {
            var sizeLine = await reader.ReadAsciiLineAsync(ct);
            var extensionStart = sizeLine.IndexOf(';');
            var sizeText = extensionStart >= 0 ? sizeLine[..extensionStart] : sizeLine;
            var size = Convert.ToInt32(sizeText.Trim(), 16);
            if (size == 0)
            {
                while ((await reader.ReadAsciiLineAsync(ct)).Length > 0)
                {
                }

                return body.ToArray();
            }

            var chunk = await reader.ReadExactAsync(size, ct);
            body.Write(chunk, 0, chunk.Length);
            await reader.ReadExactAsync(2, ct);
        }
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (bytes[i - 3] == '\r'
                && bytes[i - 2] == '\n'
                && bytes[i - 1] == '\r'
                && bytes[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static (string Method, string Path, Dictionary<string, string> Headers)
        ParseHeaders(string headerText)
    {
        using var reader = new StringReader(headerText);
        var requestLine = reader.ReadLine() ?? string.Empty;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException("Invalid HTTP request line.");

        var path = new Uri("http://127.0.0.1" + parts[1]).AbsolutePath;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return (parts[0].ToUpperInvariant(), path, headers);
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        HttpStatusCode status,
        object value,
        CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var headerText =
            $"HTTP/1.1 {(int)status} {ReasonPhrase(status)}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        var headers = Encoding.ASCII.GetBytes(headerText);
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(body, ct);
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.Unauthorized => "Unauthorized",
        HttpStatusCode.NotImplemented => "Not Implemented",
        HttpStatusCode.InternalServerError => "Internal Server Error",
        _ => status.ToString(),
    };

    private sealed record CapabilityHttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed class PrefixNetworkReader(byte[] prefix, NetworkStream stream)
    {
        private int _prefixOffset;

        public async Task<string> ReadAsciiLineAsync(CancellationToken ct)
        {
            using var line = new MemoryStream();
            while (true)
            {
                var value = await ReadByteAsync(ct);
                if (value < 0)
                    throw new ArgumentException("Unexpected end of chunked HTTP body.");

                if (value == '\n')
                    break;

                if (value != '\r')
                    line.WriteByte((byte)value);
            }

            return Encoding.ASCII.GetString(line.ToArray());
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var value = await ReadByteAsync(ct);
                if (value < 0)
                    throw new ArgumentException("Unexpected end of chunked HTTP body.");

                bytes[offset++] = (byte)value;
            }

            return bytes;
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken ct)
        {
            if (_prefixOffset < prefix.Length)
                return prefix[_prefixOffset++];

            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            return read == 0 ? -1 : buffer[0];
        }
    }
}
