using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var mode = args.Length >= 2 && args[0] == "--mode" ? args[1] : "normal";

if (mode == "early-exit")
{
    Console.WriteLine("sidecar stdout before early exit");
    Console.Error.WriteLine("sidecar stderr before early exit");
    return 23;
}

var moduleDir = ReadEnv("SHARPCLAW_MODULE_DIR");
var dataDir = ReadEnv("SHARPCLAW_MODULE_DATA_DIR");
var controlAddress = ReadEnv("SHARPCLAW_CONTROL_ADDRESS");
var token = ReadEnv("SHARPCLAW_CONTROL_TOKEN");
var moduleId = ReadEnv("SHARPCLAW_MODULE_ID");
var runtime = ReadEnv("SHARPCLAW_MODULE_RUNTIME");
var hostCapabilitiesAddress = Environment.GetEnvironmentVariable("SHARPCLAW_HOST_CAPABILITIES_ADDRESS");
var hostCapabilitiesToken = Environment.GetEnvironmentVariable("SHARPCLAW_HOST_CAPABILITIES_TOKEN");
var toolPrefix = Environment.GetEnvironmentVariable("SHARPCLAW_TEST_TOOL_PREFIX") ?? "snm";

Console.WriteLine(
    $"ENV|moduleDir={moduleDir}|dataDir={dataDir}|control={controlAddress}|token={token}|moduleId={moduleId}|runtime={runtime}|hostCapabilities={hostCapabilitiesAddress}|hostCapabilitiesToken={hostCapabilitiesToken}");
Console.Out.Flush();

if (mode == "never-ready")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var uri = new Uri(controlAddress);
var listener = new TcpListener(IPAddress.Loopback, uri.Port);
listener.Start();

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
_ = Task.Run(async () =>
{
    try
    {
        while (!stop.Task.IsCompleted)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleAsync(client));
        }
    }
    catch (SocketException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
});

await stop.Task;
listener.Stop();
return 0;

async Task HandleAsync(TcpClient client)
{
    await using var stream = client.GetStream();
    using (client)
    {
        var request = await ReadRequestAsync(stream);
        if (!string.Equals(
                request.Headers.GetValueOrDefault("X-SharpClaw-Control-Token"),
                token,
                StringComparison.Ordinal))
        {
            await WriteTextAsync(stream, 401, "Unauthorized", "bad token", "text/plain");
            return;
        }

        switch (request.Path)
        {
            case "/.sharpclaw/handshake":
                await WriteJsonAsync(stream, new
                {
                    protocolVersion = 1,
                    moduleId,
                    toolPrefix,
                    runtime,
                    runtimeVersion = "test-runtime",
                    capabilities = new[]
                    {
                        "endpoints",
                        "jobTools",
                        "inlineTools",
                        "streamingTools",
                        "protocolContracts",
                        "moduleContributionDescriptors",
                        "frontendContributions",
                        "lifecycleHooks",
                        "taskRuntime",
                        "providerPlugins",
                    },
                });
                break;

            case "/.sharpclaw/discovery":
                await WriteJsonAsync(stream, new
                {
                    endpoints = new[]
                    {
                        new
                        {
                            method = "GET",
                            routePattern = "/modules/sample/ping",
                            responseMode = "json",
                        },
                        new
                        {
                            method = "POST",
                            routePattern = "/modules/sample/echo",
                            responseMode = "json",
                        },
                        new
                        {
                            method = "GET",
                            routePattern = "/modules/sample/static/hello.txt",
                            responseMode = "static",
                        },
                        new
                        {
                            method = "GET",
                            routePattern = "/modules/sample/stream",
                            responseMode = "stream",
                        },
                        new
                        {
                            method = "GET",
                            routePattern = "/modules/sample/ws",
                            responseMode = "websocket",
                        },
                    },
                    tools = new[]
                    {
                        new
                        {
                            name = "sample_job",
                            description = "Sample foreign job tool.",
                            parametersSchema = EmptyObjectSchema(),
                            permission = new
                            {
                                isPerResource = false,
                            },
                            supportsStreaming = false,
                        },
                        new
                        {
                            name = "sample_stream",
                            description = "Sample foreign streaming job tool.",
                            parametersSchema = EmptyObjectSchema(),
                            permission = new
                            {
                                isPerResource = false,
                            },
                            supportsStreaming = true,
                        },
                    },
                    inlineTools = new[]
                    {
                        new
                        {
                            name = "sample_inline",
                            description = "Sample foreign inline tool.",
                            parametersSchema = EmptyObjectSchema(),
                        },
                    },
                    protocolContracts = new[]
                    {
                        new
                        {
                            contractName = "editor_bridge",
                            schema = EmptyObjectSchema(),
                            operations = new[]
                            {
                                new
                                {
                                    name = "open_file",
                                    parametersSchema = EmptyObjectSchema(),
                                    resultSchema = EmptyObjectSchema(),
                                    description = "Open a file in an editor.",
                                },
                            },
                            description = "Sample editor bridge protocol contract.",
                        },
                    },
                    requiredProtocolContracts = new[]
                    {
                        new
                        {
                            contractName = "theme_bridge",
                            optional = true,
                            description = "Optional theme bridge sample dependency.",
                        },
                    },
                    headerTags = new[]
                    {
                        new
                        {
                            name = "sample_header",
                            supportsContext = true,
                        },
                    },
                    resourceTypes = new[]
                    {
                        new
                        {
                            resourceType = "SampleResource",
                            grantLabel = "Sample Resource",
                            delegateMethodName = "AccessSampleResourceAsync",
                            defaultResourceKey = "sample",
                            supportsLookupItems = true,
                        },
                    },
                    globalFlags = new[]
                    {
                        new
                        {
                            flagKey = "CanUseSampleForeign",
                            displayName = "Use Sample Foreign",
                            description = "Use sample foreign module capabilities.",
                            delegateMethodName = "UseSampleForeignAsync",
                        },
                    },
                    uiContributions = new[]
                    {
                        new
                        {
                            contributionPoint = "settings_sidebar",
                            elementType = "button",
                            elementId = "sample-sidecar",
                            label = "Sample Sidecar",
                            actionToolName = "sample_job",
                        },
                    },
                    frontendContributions = new[]
                    {
                        new
                        {
                            id = "sample.settings",
                            moduleId,
                            point = "SettingsPage",
                            builderKey = "sample-list",
                            label = "Sample Foreign",
                            requiredModuleId = moduleId,
                            order = 10,
                            list = new
                            {
                                listInternalApiPath = "/modules/sample/ping",
                                emptyText = "No sample resources.",
                                columns = new[]
                                {
                                    new
                                    {
                                        key = "name",
                                        label = "Name",
                                    },
                                },
                            },
                        },
                    },
                    storageContracts = new[]
                    {
                        new
                        {
                            moduleId,
                            storageName = "sample_records",
                            operations = new[]
                            {
                                new { name = "get" },
                                new { name = "upsert" },
                                new { name = "batchUpsert" },
                                new { name = "delete" },
                                new { name = "batchDelete" },
                                new { name = "list" },
                                new { name = "query" },
                                new { name = "claim" },
                            },
                            indexes = new[]
                            {
                                new
                                {
                                    name = "status",
                                    valueKind = "String",
                                    allowsEquality = true,
                                    allowsRange = false,
                                },
                                new
                                {
                                    name = "updatedAt",
                                    valueKind = "DateTime",
                                    allowsEquality = true,
                                    allowsRange = true,
                                },
                            },
                            maxDocumentBytes = 65536,
                            maxBatchSize = 100,
                        },
                    },
                    cliCommands = new[]
                    {
                        new
                        {
                            name = "sample",
                            aliases = new[] { "smp" },
                            scope = "TopLevel",
                            description = "Sample foreign CLI command.",
                            usageLines = new[] { "sample ping" },
                        },
                    },
                    taskParser = new
                    {
                        operationKeyMappings = new[]
                        {
                            new
                            {
                                methodName = "SampleTaskOperation",
                                statementKey = "sample.task.operation",
                                moduleId,
                            },
                        },
                        eventTriggerMappings = new[]
                        {
                            new
                            {
                                methodName = "OnSample",
                                triggerKey = "sample.trigger",
                                moduleId,
                            },
                        },
                        singleArgExpressionMethods = new[] { "SampleTaskOperation" },
                        triggerAttributeHandlers = new[]
                        {
                            new
                            {
                                name = "SampleTrigger",
                                namedStringArgs = new[] { "Name" },
                            },
                        },
                    },
                    taskOperationDescriptors = new[]
                    {
                        new
                        {
                            methodName = "SampleTaskOperation",
                            operationKey = "sample.task.operation",
                            ownerId = moduleId,
                            firstArgIsExpression = true,
                        },
                    },
                    taskOperationExecutors = new[]
                    {
                        new
                        {
                            moduleId,
                            operationKeys = new[] { "sample.task.operation" },
                            supportsInvocation = true,
                        },
                    },
                    taskTriggerSources = new[]
                    {
                        new
                        {
                            triggerKeys = new[] { "sample.trigger" },
                            ownsBindingPersistence = false,
                        },
                    },
                    taskTriggerBindingSideEffects = new[]
                    {
                        new
                        {
                            triggerKey = "sample.trigger",
                        },
                    },
                    taskMetricProviders = new[]
                    {
                        new
                        {
                            metricName = "sample.metric",
                            description = "Sample sidecar metric.",
                        },
                    },
                    taskEventSinks = new[]
                    {
                        new
                        {
                            subscribedEvents = "AllModuleEvents",
                        },
                    },
                    providerPlugins = new[]
                    {
                        new
                        {
                            providerKey = "sample-foreign-provider",
                            displayName = "Sample Foreign Provider",
                            ownerModuleId = moduleId,
                            requiresEndpoint = true,
                            supportsAutomaticEndpointDiscovery = true,
                            isSeedable = true,
                            requiresApiKey = false,
                            supportsNativeToolCalling = true,
                            supportsDeviceCodeFlow = true,
                            supportsCostFeed = true,
                            costFeedPermissionDeniedNote = "Sample foreign provider requires billing access.",
                            costSeeds = new[]
                            {
                                new
                                {
                                    modelName = "sample-model",
                                    inputCostPerMillion = 1.25m,
                                    outputCostPerMillion = 2.50m,
                                    currency = "usd",
                                },
                            },
                            parameterSpec = new
                            {
                                providerName = "Sample Foreign Provider",
                                supportsTemperature = true,
                                temperatureMin = 0.0f,
                                temperatureMax = 1.0f,
                                supportsTopP = true,
                                topPMin = 0.0f,
                                topPMax = 1.0f,
                                supportsTopK = false,
                                topKMin = 1,
                                topKMax = 1,
                                supportsFrequencyPenalty = true,
                                frequencyPenaltyMin = -1.0f,
                                frequencyPenaltyMax = 1.0f,
                                supportsPresencePenalty = true,
                                presencePenaltyMin = -1.0f,
                                presencePenaltyMax = 1.0f,
                                supportsStop = true,
                                maxStopSequences = 4,
                                supportsSeed = true,
                                supportsResponseFormat = true,
                                rejectsJsonObjectResponseFormat = false,
                                onlyJsonObjectResponseFormat = false,
                                supportsReasoningEffort = true,
                                reasoningEffortInformationalOnly = false,
                                validReasoningEffortValues = new[] { "none", "low", "medium" },
                                supportsToolChoice = true,
                                supportsStrictTools = true,
                            },
                        },
                    },
                });
                break;

            case "/.sharpclaw/health":
                await WriteJsonAsync(stream, new
                {
                    isHealthy = true,
                    message = "ready",
                });
                break;

            case "/.sharpclaw/initialize":
                await WriteJsonAsync(stream, new
                {
                    accepted = true,
                    message = "initialized",
                });
                break;

            case "/.sharpclaw/shutdown":
                await WriteJsonAsync(stream, new
                {
                    accepted = true,
                    message = "stopping",
                });
                if (mode != "ignore-shutdown")
                    stop.TrySetResult();
                break;

            case "/.sharpclaw/tools/execute":
                await WriteJsonAsync(stream, new
                {
                    result = BuildToolResult("job", request.Body),
                });
                break;

            case "/.sharpclaw/inline-tools/execute":
                await WriteJsonAsync(stream, new
                {
                    result = BuildToolResult("inline", request.Body),
                });
                break;

            case "/.sharpclaw/tools/stream":
                await WriteNdjsonAsync(
                    stream,
                    new { delta = "first:" },
                    new { delta = "second" },
                    new { isFinal = true });
                break;

            case "/.sharpclaw/contracts/invoke":
                await WriteJsonAsync(stream, new
                {
                    result = BuildContractResult(request.Body),
                });
                break;

            case "/.sharpclaw/header-tags/resolve":
                await WriteJsonAsync(stream, new
                {
                    value = BuildHeaderTagResult(request.Body),
                });
                break;

            case "/.sharpclaw/resources/ids":
                await WriteJsonAsync(stream, new
                {
                    ids = new[]
                    {
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    },
                });
                break;

            case "/.sharpclaw/resources/lookup":
                await WriteJsonAsync(stream, new
                {
                    items = new[]
                    {
                        new
                        {
                            id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            name = "Sample One",
                        },
                    },
                });
                break;

            case "/.sharpclaw/cli/execute":
                await WriteJsonAsync(stream, new
                {
                    success = true,
                    stdout = BuildCliResult(request.Body),
                    stderr = "",
                });
                break;

            case "/.sharpclaw/tasks/operations/execute":
                await WriteJsonAsync(stream, new
                {
                    result = "Continue",
                    @continue = true,
                    variableUpdates = new Dictionary<string, object?>
                    {
                        ["sidecarOperation"] = "executed",
                    },
                    resultVariableValue = "operation-result",
                    logs = new[] { "operation log" },
                    outputJson = """{"sidecar":true}""",
                });
                break;

            case "/.sharpclaw/tasks/operations/invoke":
                await WriteJsonAsync(stream, new
                {
                    result = "Continue",
                    variableUpdates = new Dictionary<string, object?>
                    {
                        ["sidecarInvocation"] = "executed",
                    },
                    resultVariableValue = "invocation-result",
                    logs = new[] { "invocation log" },
                });
                break;

            case "/.sharpclaw/tasks/triggers/attributes/handle":
                await WriteJsonAsync(stream, BuildTriggerAttributeResult(request.Body));
                break;

            case "/.sharpclaw/tasks/triggers/start":
            case "/.sharpclaw/tasks/triggers/stop":
            case "/.sharpclaw/tasks/triggers/remove-bindings":
            case "/.sharpclaw/tasks/triggers/bindings/created":
            case "/.sharpclaw/tasks/triggers/bindings/removed":
            case "/.sharpclaw/tasks/events/sink":
                await WriteJsonAsync(stream, new
                {
                    accepted = true,
                });
                break;

            case "/.sharpclaw/tasks/triggers/binding-value":
                await WriteJsonAsync(stream, new
                {
                    value = "sample-value",
                });
                break;

            case "/.sharpclaw/tasks/triggers/binding-filter":
                await WriteJsonAsync(stream, new
                {
                    value = "sample-filter",
                });
                break;

            case "/.sharpclaw/tasks/triggers/sync-bindings":
                await WriteJsonAsync(stream, new
                {
                    changed = true,
                });
                break;

            case "/.sharpclaw/tasks/metrics/value":
                await WriteJsonAsync(stream, new
                {
                    value = 42.5,
                });
                break;

            case "/.sharpclaw/providers/models/list":
                await WriteJsonAsync(stream, new
                {
                    modelIds = new[] { "sample-model", "sample-vision-model" },
                });
                break;

            case "/.sharpclaw/providers/capabilities/resolve":
                await WriteJsonAsync(stream, new
                {
                    tags = new[] { "chat", "vision" },
                });
                break;

            case "/.sharpclaw/providers/chat/complete":
                await WriteJsonAsync(stream, new
                {
                    result = new
                    {
                        content = BuildProviderChatResult("chat", request.Body),
                        toolCalls = Array.Empty<object>(),
                        providerMetadataJson = """{"provider":"sample"}""",
                        usage = new
                        {
                            promptTokens = 3,
                            completionTokens = 5,
                        },
                        finishReason = "Stop",
                    },
                });
                break;

            case "/.sharpclaw/providers/chat/complete-tools":
                await WriteJsonAsync(stream, new
                {
                    result = new
                    {
                        content = BuildProviderChatResult("tools", request.Body),
                        toolCalls = new[]
                        {
                            new
                            {
                                id = "call-1",
                                name = "sample_tool",
                                argumentsJson = """{"ok":true}""",
                            },
                        },
                        usage = new
                        {
                            promptTokens = 7,
                            completionTokens = 11,
                        },
                        finishReason = "ToolCalls",
                    },
                });
                break;

            case "/.sharpclaw/providers/chat/stream-tools":
                await WriteNdjsonAsync(
                    stream,
                    new { delta = "stream " },
                    new
                    {
                        toolCallDelta = new
                        {
                            index = 0,
                            id = "call-1",
                            name = "sample_tool",
                            argumentsFragment = """{"ok":""",
                        },
                    },
                    new
                    {
                        toolCallDelta = new
                        {
                            index = 0,
                            argumentsFragment = "true}",
                        },
                    },
                    new
                    {
                        finished = new
                        {
                            content = "stream final",
                            toolCalls = new[]
                            {
                                new
                                {
                                    id = "call-1",
                                    name = "sample_tool",
                                    argumentsJson = """{"ok":true}""",
                                },
                            },
                            finishReason = "ToolCalls",
                        },
                    });
                break;

            case "/.sharpclaw/providers/device-code/start":
                await WriteJsonAsync(stream, new
                {
                    session = new
                    {
                        deviceCode = "device-code",
                        userCode = "USER-CODE",
                        verificationUri = "https://example.test/device",
                        expiresInSeconds = 900,
                        intervalSeconds = 5,
                    },
                });
                break;

            case "/.sharpclaw/providers/device-code/poll":
                await WriteJsonAsync(stream, new
                {
                    accessToken = "device-access-token",
                });
                break;

            case "/.sharpclaw/providers/costs":
                await WriteJsonAsync(stream, new
                {
                    result = new
                    {
                        totalAmount = 12.34m,
                        currency = "usd",
                        dailyBuckets = new[]
                        {
                            new
                            {
                                start = DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                                end = DateTimeOffset.Parse("2026-05-02T00:00:00Z"),
                                amount = 12.34m,
                            },
                        },
                    },
                });
                break;

            case "/.sharpclaw/providers/agent-identifier-suffix":
                await WriteJsonAsync(stream, new
                {
                    suffix = "sample-sidecar",
                });
                break;

            case "/modules/sample/ping":
                await WriteJsonAsync(stream, new
                {
                    ok = true,
                    path = request.Path,
                    query = request.Query,
                    marker = request.Headers.GetValueOrDefault("X-Test-Marker"),
                }, ("X-Sidecar", "yes"));
                break;

            case "/modules/sample/echo":
                await WriteJsonAsync(stream, new
                {
                    method = request.Method,
                    path = request.Path,
                    query = request.Query,
                    body = request.Body,
                    contentType = request.Headers.GetValueOrDefault("Content-Type"),
                });
                break;

            case "/modules/sample/static/hello.txt":
                await WriteTextAsync(
                    stream,
                    200,
                    "OK",
                    "static-parity-asset",
                    "text/plain");
                break;

            case "/modules/sample/stream":
                await WriteNdjsonAsync(
                    stream,
                    new { delta = "first:" },
                    new { delta = "second" },
                    new { isFinal = true });
                break;

            case "/modules/sample/ws":
                await HandleWebSocketEchoAsync(stream, request);
                break;

            default:
                await WriteTextAsync(stream, 404, "Not Found", "not found", "text/plain");
                break;
        }
    }
}

static string ReadEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing required environment variable '{name}'.");

static object EmptyObjectSchema() => new
{
    type = "object",
    properties = new { },
};

static string BuildToolResult(string kind, string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var toolName = root.GetProperty("toolName").GetString();
    var parameters = root.GetProperty("parameters").GetRawText();
    return $"{kind}:{toolName}:{parameters}";
}

static object BuildContractResult(string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    return new
    {
        contractName = root.GetProperty("contractName").GetString(),
        operation = root.GetProperty("operation").GetString(),
        parameters = root.GetProperty("parameters").Clone(),
    };
}

static string BuildHeaderTagResult(string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    return "header:" + root.GetProperty("name").GetString();
}

static string BuildCliResult(string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var command = root.GetProperty("commandName").GetString();
    var args = root.GetProperty("args").EnumerateArray()
        .Select(arg => arg.GetString())
        .Where(arg => arg is not null);
    return $"cli:{command}:{string.Join(",", args)}";
}

static string BuildProviderChatResult(string kind, string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var providerKey = root.GetProperty("providerKey").GetString();
    var model = root.GetProperty("model").GetString();
    var messageCount = root.TryGetProperty("messages", out var messages)
        ? messages.GetArrayLength()
        : 0;
    return $"{kind}:{providerKey}:{model}:{messageCount}";
}

static object BuildTriggerAttributeResult(string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var context = root.GetProperty("context");
    var name = context.GetProperty("namedStringArgs").TryGetProperty("Name", out var nameElement)
        ? nameElement.GetString()
        : null;

    return new
    {
        trigger = new
        {
            triggerKey = "sample.trigger",
            line = context.GetProperty("line").GetInt32(),
            parameters = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["attribute"] = context.GetProperty("attributeName").GetString(),
            },
        },
        diagnostics = Array.Empty<object>(),
    };
}

static async Task HandleWebSocketEchoAsync(NetworkStream stream, SidecarRequest request)
{
    if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
    {
        await WriteTextAsync(stream, 400, "Bad Request", "WebSocket connections only.", "text/plain");
        return;
    }

    var accept = Convert.ToBase64String(SHA1.HashData(
        Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    var headers = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        $"Sec-WebSocket-Accept: {accept}\r\n" +
        "\r\n");
    await stream.WriteAsync(headers);

    using var socket = WebSocket.CreateFromStream(
        stream,
        isServer: true,
        subProtocol: null,
        keepAliveInterval: TimeSpan.FromSeconds(30));
    var buffer = new byte[16 * 1024];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "closing",
                CancellationToken.None);
            return;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var response = Encoding.UTF8.GetBytes("sidecar:" + text);
        await socket.SendAsync(
            response,
            result.MessageType,
            endOfMessage: true,
            CancellationToken.None);
    }
}

static async Task<SidecarRequest> ReadRequestAsync(NetworkStream stream)
{
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var requestLine = await reader.ReadLineAsync() ?? string.Empty;
    var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var method = parts.Length >= 1 ? parts[0] : "GET";
    var requestUri = parts.Length >= 2 ? new Uri("http://127.0.0.1" + parts[1]) : new Uri("http://127.0.0.1/");
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    while (await reader.ReadLineAsync() is { } line && line.Length > 0)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
            continue;

        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }

    var body = string.Empty;
    if (headers.TryGetValue("Content-Length", out var contentLengthText)
        && int.TryParse(contentLengthText, out var contentLength)
        && contentLength > 0)
    {
        var buffer = new char[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(offset, contentLength - offset));
            if (read == 0)
                break;

            offset += read;
        }

        body = new string(buffer, 0, offset);
    }

    return new SidecarRequest(
        method,
        requestUri.AbsolutePath,
        requestUri.Query,
        headers,
        body);
}

static Task WriteJsonAsync(
    NetworkStream stream,
    object value,
    params (string Name, string Value)[] headers) =>
    WriteTextAsync(
        stream,
        200,
        "OK",
        JsonSerializer.Serialize(value),
        "application/json",
        headers);

static async Task WriteNdjsonAsync(
    NetworkStream stream,
    params object[] messages)
{
    var body = string.Concat(messages.Select(message =>
        JsonSerializer.Serialize(message) + "\n"));
    await WriteTextAsync(
        stream,
        200,
        "OK",
        body,
        "application/x-ndjson");
}

static async Task WriteTextAsync(
    NetworkStream stream,
    int statusCode,
    string reasonPhrase,
    string text,
    string contentType,
    params (string Name, string Value)[] extraHeaders)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    var headerText =
        $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
        $"Content-Type: {contentType}; charset=utf-8\r\n" +
        $"Content-Length: {bytes.Length}\r\n" +
        "Connection: close\r\n";
    foreach (var (name, value) in extraHeaders)
        headerText += $"{name}: {value}\r\n";
    headerText += "\r\n";
    var headers = Encoding.ASCII.GetBytes(
        headerText);
    await stream.WriteAsync(headers);
    await stream.WriteAsync(bytes);
}

internal sealed record SidecarRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
