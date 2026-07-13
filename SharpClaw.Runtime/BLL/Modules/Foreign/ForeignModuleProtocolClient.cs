using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleProtocolClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly string _controlToken;

    public ForeignModuleProtocolClient(HttpClient httpClient, string controlToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);

        _httpClient = httpClient;
        _controlToken = controlToken;
    }

    public async Task<ForeignModuleHandshakeResponse> HandshakeAsync(
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        string? hostVersion = null,
        CancellationToken ct = default)
    {
        var request = new ForeignModuleHandshakeRequest(
            ForeignModuleProtocol.Version,
            manifest.Id,
            manifest.ToolPrefix,
            hostVersion);

        var response = await PostAsync<ForeignModuleHandshakeRequest, ForeignModuleHandshakeResponse>(
            ForeignModuleProtocol.HandshakePath,
            request,
            ct);

        ValidateHandshake(manifest, runtimeInfo, response);
        return response;
    }

    public Task<ForeignModuleHealthResponse> HealthAsync(CancellationToken ct = default) =>
        GetAsync<ForeignModuleHealthResponse>(ForeignModuleProtocol.HealthPath, ct);

    public Task<ForeignModuleDiscoveryResponse> DiscoverAsync(CancellationToken ct = default) =>
        GetAsync<ForeignModuleDiscoveryResponse>(ForeignModuleProtocol.DiscoveryPath, ct);

    public Task<ForeignModuleLifecycleResponse> InitializeAsync(
        ModuleManifest manifest,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleLifecycleRequest, ForeignModuleLifecycleResponse>(
            ForeignModuleProtocol.InitializePath,
            new ForeignModuleLifecycleRequest(ForeignModuleProtocol.Version, manifest.Id),
            ct);

    public Task<ForeignModuleLifecycleResponse> ShutdownAsync(
        ModuleManifest manifest,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleLifecycleRequest, ForeignModuleLifecycleResponse>(
            ForeignModuleProtocol.ShutdownPath,
            new ForeignModuleLifecycleRequest(ForeignModuleProtocol.Version, manifest.Id),
            ct);

    public Task<ForeignModuleToolExecutionResponse> ExecuteToolAsync(
        ModuleManifest manifest,
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleToolExecutionRequest, ForeignModuleToolExecutionResponse>(
            ForeignModuleProtocol.ToolExecutePath,
            new ForeignModuleToolExecutionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                toolName,
                parameters,
                ForeignModuleProtocolModelMapper.ToForeignModuleAgentJobContext(job)),
            ct);

    public Task<ForeignModuleToolCompletionBehaviorResponse> GetToolCompletionBehaviorAsync(
        ModuleManifest manifest,
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleToolCompletionBehaviorRequest, ForeignModuleToolCompletionBehaviorResponse>(
            ForeignModuleProtocol.ToolCompletionBehaviorPath,
            new ForeignModuleToolCompletionBehaviorRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                toolName,
                parameters,
                ForeignModuleProtocolModelMapper.ToForeignModuleAgentJobContext(job)),
            ct);

    public Task<ForeignModuleToolExecutionResponse> ExecuteInlineToolAsync(
        ModuleManifest manifest,
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleInlineToolExecutionRequest, ForeignModuleToolExecutionResponse>(
            ForeignModuleProtocol.InlineToolExecutePath,
            new ForeignModuleInlineToolExecutionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                toolName,
                parameters,
                ForeignModuleProtocolModelMapper.ToForeignModuleInlineToolContext(context)),
            ct);

    public Task<ForeignModuleProtocolContractInvocationResponse> InvokeProtocolContractAsync(
        ModuleManifest manifest,
        string contractName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleProtocolContractInvocationRequest, ForeignModuleProtocolContractInvocationResponse>(
            ForeignModuleProtocol.ContractInvokePath,
            new ForeignModuleProtocolContractInvocationRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                contractName,
                operation,
                parameters),
            ct);

    public Task<ForeignModuleHeaderTagResolveResponse> ResolveHeaderTagAsync(
        ModuleManifest manifest,
        string name,
        ModuleHeaderTagContext? context,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleHeaderTagResolveRequest, ForeignModuleHeaderTagResolveResponse>(
            ForeignModuleProtocol.HeaderTagResolvePath,
            new ForeignModuleHeaderTagResolveRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                name,
                context),
            ct);

    public async Task<List<Guid>> LoadResourceIdsAsync(
        ModuleManifest manifest,
        string resourceType,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleResourceRequest, ForeignModuleResourceIdsResponse>(
            ForeignModuleProtocol.ResourceIdsPath,
            new ForeignModuleResourceRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                resourceType),
            ct);
        return [.. response.Ids];
    }

    public async Task<List<ForeignModuleResourceLookupItem>> LoadResourceLookupItemsAsync(
        ModuleManifest manifest,
        string resourceType,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleResourceRequest, ForeignModuleResourceLookupResponse>(
            ForeignModuleProtocol.ResourceLookupPath,
            new ForeignModuleResourceRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                resourceType),
            ct);
        return [.. response.Items];
    }

    public Task<ForeignModuleCliExecutionResponse> ExecuteCliCommandAsync(
        ModuleManifest manifest,
        string commandName,
        IReadOnlyList<string> args,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleCliExecutionRequest, ForeignModuleCliExecutionResponse>(
            ForeignModuleProtocol.CliExecutePath,
            new ForeignModuleCliExecutionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                commandName,
                args),
            ct);

    public Task<ForeignModuleTaskOperationExecutionResponse> ExecuteTaskOperationAsync(
        ModuleManifest manifest,
        string operationKey,
        ForeignModuleTaskOperationExecutionContextSnapshot context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskOperationExecutionRequest, ForeignModuleTaskOperationExecutionResponse>(
            ForeignModuleProtocol.TaskOperationExecutePath,
            new ForeignModuleTaskOperationExecutionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                operationKey,
                context,
                arguments,
                expression,
                resultVariable),
            ct);

    public Task<ForeignModuleTaskOperationExecutionResponse> ExecuteTaskOperationInvocationAsync(
        ModuleManifest manifest,
        ITaskStatementInvocation statement,
        ForeignModuleTaskOperationExecutionContextSnapshot context,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskOperationInvocationRequest, ForeignModuleTaskOperationExecutionResponse>(
            ForeignModuleProtocol.TaskOperationInvokePath,
            new ForeignModuleTaskOperationInvocationRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                ForeignModuleTaskStatementInvocationDescriptor.From(statement),
                context),
            ct);

    public Task<ForeignModuleTaskTriggerAttributeHandleResponse> HandleTaskTriggerAttributeAsync(
        ModuleManifest manifest,
        string handlerName,
        ForeignModuleTaskTriggerAttributeContextDescriptor context,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerAttributeHandleRequest, ForeignModuleTaskTriggerAttributeHandleResponse>(
            ForeignModuleProtocol.TaskTriggerAttributeHandlePath,
            new ForeignModuleTaskTriggerAttributeHandleRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                handlerName,
                context),
            ct);

    public Task<ForeignModuleTaskAckResponse> StartTaskTriggerSourceAsync(
        ModuleManifest manifest,
        IReadOnlyList<string> triggerKeys,
        IReadOnlyList<ForeignModuleTaskTriggerSourceContextDescriptor> contexts,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerStartRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskTriggerStartPath,
            new ForeignModuleTaskTriggerStartRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKeys,
                contexts),
            ct);

    public Task<ForeignModuleTaskAckResponse> StopTaskTriggerSourceAsync(
        ModuleManifest manifest,
        IReadOnlyList<string> triggerKeys,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerStopRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskTriggerStopPath,
            new ForeignModuleTaskTriggerStopRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKeys),
            ct);

    public async Task<string?> GetTaskTriggerBindingValueAsync(
        ModuleManifest manifest,
        string triggerKey,
        TaskTriggerDefinition definition,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleTaskTriggerDefinitionRequest, ForeignModuleTaskTriggerBindingValueResponse>(
            ForeignModuleProtocol.TaskTriggerBindingValuePath,
            new ForeignModuleTaskTriggerDefinitionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKey,
                definition),
            ct);
        return response.Value;
    }

    public async Task<string?> GetTaskTriggerBindingFilterAsync(
        ModuleManifest manifest,
        string triggerKey,
        TaskTriggerDefinition definition,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleTaskTriggerDefinitionRequest, ForeignModuleTaskTriggerBindingValueResponse>(
            ForeignModuleProtocol.TaskTriggerBindingFilterPath,
            new ForeignModuleTaskTriggerDefinitionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKey,
                definition),
            ct);
        return response.Value;
    }

    public async Task<bool> SyncTaskTriggerBindingsAsync(
        ModuleManifest manifest,
        IReadOnlyList<string> triggerKeys,
        TaskDefinitionDescriptor definition,
        IReadOnlyList<TaskTriggerDefinition> ownedTriggers,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleTaskTriggerSyncBindingsRequest, ForeignModuleTaskTriggerSyncBindingsResponse>(
            ForeignModuleProtocol.TaskTriggerSyncBindingsPath,
            new ForeignModuleTaskTriggerSyncBindingsRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKeys,
                definition,
                ownedTriggers),
            ct);
        return response.Changed;
    }

    public Task<ForeignModuleTaskAckResponse> RemoveTaskTriggerBindingsAsync(
        ModuleManifest manifest,
        IReadOnlyList<string> triggerKeys,
        Guid definitionId,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerRemoveBindingsRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskTriggerRemoveBindingsPath,
            new ForeignModuleTaskTriggerRemoveBindingsRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKeys,
                definitionId),
            ct);

    public Task<ForeignModuleTaskAckResponse> NotifyTaskTriggerBindingCreatedAsync(
        ModuleManifest manifest,
        string triggerKey,
        TaskDefinitionDescriptor definition,
        TaskTriggerDefinition trigger,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerBindingCreatedRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskTriggerBindingCreatedPath,
            new ForeignModuleTaskTriggerBindingCreatedRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKey,
                definition,
                trigger,
                binding),
            ct);

    public Task<ForeignModuleTaskAckResponse> NotifyTaskTriggerBindingRemovedAsync(
        ModuleManifest manifest,
        string triggerKey,
        TaskTriggerBindingDescriptor binding,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskTriggerBindingRemovedRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskTriggerBindingRemovedPath,
            new ForeignModuleTaskTriggerBindingRemovedRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                triggerKey,
                binding),
            ct);

    public async Task<double> GetTaskMetricValueAsync(
        ModuleManifest manifest,
        string metricName,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleTaskMetricValueRequest, ForeignModuleTaskMetricValueResponse>(
            ForeignModuleProtocol.TaskMetricValuePath,
            new ForeignModuleTaskMetricValueRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                metricName),
            ct);
        return response.Value;
    }

    public Task<ForeignModuleTaskAckResponse> SendTaskEventAsync(
        ModuleManifest manifest,
        SharpClawEvent evt,
        CancellationToken ct = default) =>
        PostAsync<ForeignModuleTaskEventSinkRequest, ForeignModuleTaskAckResponse>(
            ForeignModuleProtocol.TaskEventSinkPath,
            new ForeignModuleTaskEventSinkRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                evt),
            ct);

    public async Task<IReadOnlyList<string>> ListProviderModelIdsAsync(
        ModuleManifest manifest,
        string providerKey,
        string? endpoint,
        string apiKey,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderModelListRequest, ForeignModuleProviderModelListResponse>(
            ForeignModuleProtocol.ProviderModelsListPath,
            new ForeignModuleProviderModelListRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                endpoint,
                apiKey),
            ct);
        return response.ModelIds;
    }

    public async Task<HashSet<string>> ResolveProviderCapabilitiesAsync(
        ModuleManifest manifest,
        string providerKey,
        string modelName,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderCapabilitiesResolveRequest, ForeignModuleProviderCapabilitiesResolveResponse>(
            ForeignModuleProtocol.ProviderCapabilitiesResolvePath,
            new ForeignModuleProviderCapabilitiesResolveRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                modelName),
            ct);
        return new HashSet<string>(response.Tags, StringComparer.Ordinal);
    }

    public async Task<ChatCompletionResult> CompleteProviderChatAsync(
        ModuleManifest manifest,
        string providerKey,
        string? endpoint,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderChatCompletionRequest, ForeignModuleProviderChatCompletionResponse>(
            ForeignModuleProtocol.ProviderChatCompletionPath,
            new ForeignModuleProviderChatCompletionRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                endpoint,
                apiKey,
                model,
                systemPrompt,
                messages,
                maxCompletionTokens,
                providerParameters,
                completionParameters),
            ct);
        return response.Result;
    }

    public async Task<ChatCompletionResult> CompleteProviderChatWithToolsAsync(
        ModuleManifest manifest,
        string providerKey,
        string? endpoint,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderChatCompletionWithToolsRequest, ForeignModuleProviderChatCompletionResponse>(
            ForeignModuleProtocol.ProviderChatCompletionWithToolsPath,
            new ForeignModuleProviderChatCompletionWithToolsRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                endpoint,
                apiKey,
                model,
                systemPrompt,
                messages,
                tools,
                maxCompletionTokens,
                providerParameters,
                completionParameters),
            ct);
        return response.Result;
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamProviderChatWithToolsAsync(
        ModuleManifest manifest,
        string providerKey,
        string? endpoint,
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        Dictionary<string, JsonElement>? providerParameters,
        CompletionParameters? completionParameters,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, ForeignModuleProtocol.ProviderStreamChatCompletionWithToolsPath);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new ForeignModuleProviderChatCompletionWithToolsRequest(
                    ForeignModuleProtocol.Version,
                    manifest.Id,
                    providerKey,
                    endpoint,
                    apiKey,
                    model,
                    systemPrompt,
                    messages,
                    tools,
                    maxCompletionTokens,
                    providerParameters,
                    completionParameters),
                JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(ct);
            throw new ForeignModuleProtocolException(
                $"Foreign module control request {request.Method} {request.RequestUri} failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ChatStreamChunk chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(line, JsonOptions)
                    ?? throw new JsonException("Stream chunk deserialized to null.");
            }
            catch (JsonException ex)
            {
                throw new ForeignModuleProtocolException(
                    $"Foreign module provider '{providerKey}' returned invalid stream JSON.",
                    response.StatusCode,
                    line,
                    ex);
            }

            yield return chunk;

            if (chunk.IsFinished)
                yield break;
        }
    }

    public async Task<DeviceCodeSession> StartProviderDeviceCodeAsync(
        ModuleManifest manifest,
        string providerKey,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderDeviceCodeStartRequest, ForeignModuleProviderDeviceCodeStartResponse>(
            ForeignModuleProtocol.ProviderDeviceCodeStartPath,
            new ForeignModuleProviderDeviceCodeStartRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey),
            ct);
        return response.Session;
    }

    public async Task<string?> PollProviderDeviceCodeAsync(
        ModuleManifest manifest,
        string providerKey,
        DeviceCodeSession session,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderDeviceCodePollRequest, ForeignModuleProviderDeviceCodePollResponse>(
            ForeignModuleProtocol.ProviderDeviceCodePollPath,
            new ForeignModuleProviderDeviceCodePollRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                session),
            ct);
        return response.AccessToken;
    }

    public async Task<ProviderCostResult?> GetProviderCostsAsync(
        ModuleManifest manifest,
        string providerKey,
        string apiKey,
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderCostFeedRequest, ForeignModuleProviderCostFeedResponse>(
            ForeignModuleProtocol.ProviderCostFeedPath,
            new ForeignModuleProviderCostFeedRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                apiKey,
                startTime,
                endTime),
            ct);
        return response.Result;
    }

    public async Task<string> GetProviderAgentIdentifierSuffixAsync(
        ModuleManifest manifest,
        string providerKey,
        string providerName,
        Guid modelId,
        CancellationToken ct = default)
    {
        var response = await PostAsync<ForeignModuleProviderAgentIdentifierSuffixRequest, ForeignModuleProviderAgentIdentifierSuffixResponse>(
            ForeignModuleProtocol.ProviderAgentIdentifierSuffixPath,
            new ForeignModuleProviderAgentIdentifierSuffixRequest(
                ForeignModuleProtocol.Version,
                manifest.Id,
                providerKey,
                providerName,
                modelId),
            ct);
        return response.Suffix;
    }

    public async IAsyncEnumerable<string> ExecuteToolStreamingAsync(
        ModuleManifest manifest,
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, ForeignModuleProtocol.ToolStreamPath);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new ForeignModuleToolExecutionRequest(
                    ForeignModuleProtocol.Version,
                    manifest.Id,
                    toolName,
                    parameters,
                    ForeignModuleProtocolModelMapper.ToForeignModuleAgentJobContext(job)),
                JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(ct);
            throw new ForeignModuleProtocolException(
                $"Foreign module control request {request.Method} {request.RequestUri} failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ForeignModuleToolStreamEvent message;
            try
            {
                message = JsonSerializer.Deserialize<ForeignModuleToolStreamEvent>(line, JsonOptions)
                    ?? throw new JsonException("Stream event deserialized to null.");
            }
            catch (JsonException ex)
            {
                throw new ForeignModuleProtocolException(
                    $"Foreign module streaming tool '{toolName}' returned invalid stream JSON.",
                    response.StatusCode,
                    line,
                    ex);
            }

            if (!string.IsNullOrEmpty(message.Error))
                throw new ForeignModuleProtocolException(
                    $"Foreign module streaming tool '{toolName}' failed: {message.Error}");

            if (message.Delta is not null)
                yield return message.Delta;

            if (message.Result is not null)
                yield return message.Result;

            if (message.IsFinal)
                yield break;
        }
    }

    private static void ValidateHandshake(
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        ForeignModuleHandshakeResponse response)
    {
        if (response.ProtocolVersion != ForeignModuleProtocol.Version)
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module '{manifest.Id}' speaks protocol version {response.ProtocolVersion}, " +
                $"but this host requires version {ForeignModuleProtocol.Version}.");
        }

        if (!string.Equals(response.ModuleId, manifest.Id, StringComparison.Ordinal))
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module handshake id '{response.ModuleId}' does not match manifest id '{manifest.Id}'.");
        }

        if (!string.Equals(response.ToolPrefix, manifest.ToolPrefix, StringComparison.Ordinal))
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module handshake tool prefix '{response.ToolPrefix}' does not match manifest toolPrefix '{manifest.ToolPrefix}'.");
        }

        if (!string.Equals(
                ModuleManifestRuntimeInfo.Normalize(response.Runtime),
                runtimeInfo.Runtime,
                StringComparison.Ordinal))
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module '{manifest.Id}' reports runtime '{response.Runtime}', " +
                $"but manifest declares runtime '{runtimeInfo.Runtime}'.");
        }
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        return await SendAsync<TResponse>(request, ct);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        var json = JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync<TResponse>(request, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ForeignModuleProtocol.TokenHeaderName, _controlToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module control request {request.Method} {request.RequestUri} failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module control request {request.Method} {request.RequestUri} returned an empty response.");
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(body, JsonOptions)
                ?? throw new JsonException("Response body deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new ForeignModuleProtocolException(
                $"Foreign module control request {request.Method} {request.RequestUri} returned invalid JSON.",
                response.StatusCode,
                body,
                ex);
        }
    }
}
