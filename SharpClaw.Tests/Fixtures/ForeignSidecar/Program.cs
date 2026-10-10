using System.Globalization;
using SharpClaw.TestFixtures.ForeignSidecar;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var mode = args.Length >= 2 && string.Equals(args[0], "--mode", StringComparison.Ordinal) ? args[1] : "normal";

if (string.Equals(mode, "early-exit", StringComparison.Ordinal))
{
    // This protocol-test marker is asserted verbatim by sidecar process tests.
#pragma warning disable CA1303
    await Console.Out.WriteLineAsync("sidecar stdout before early exit").ConfigureAwait(false);
#pragma warning restore CA1303
    await Console.Error.WriteLineAsync("sidecar stderr before early exit").ConfigureAwait(false);
    return 23;
}

var registrationDir = ReadEnv("SHARPCLAW_MODULE_DIR");
var dataDir = ReadEnv("SHARPCLAW_MODULE_DATA_DIR");
var controlAddress = ReadEnv("SHARPCLAW_CONTROL_ADDRESS");
var token = ReadEnv("SHARPCLAW_CONTROL_TOKEN");
var SourceId = ReadEnv("SHARPCLAW_MODULE_ID");
var runtime = ReadEnv("SHARPCLAW_MODULE_RUNTIME");
var hostCapabilitiesAddress = Environment.GetEnvironmentVariable("SHARPCLAW_HOST_CAPABILITIES_ADDRESS");
var hostCapabilitiesToken = Environment.GetEnvironmentVariable("SHARPCLAW_HOST_CAPABILITIES_TOKEN");
var toolPrefix = Environment.GetEnvironmentVariable("SHARPCLAW_TEST_TOOL_PREFIX") ?? "sdm";

await Console.Out.WriteLineAsync(
    $"ENV|registrationDir={registrationDir}|dataDir={dataDir}|control={controlAddress}|token={token}|SourceId={SourceId}|runtime={runtime}|hostCapabilities={hostCapabilitiesAddress}|hostCapabilitiesToken={hostCapabilitiesToken}").ConfigureAwait(false);
await Console.Out.FlushAsync().ConfigureAwait(false);

if (string.Equals(mode, "never-ready", StringComparison.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    return 0;
}

var uri = new Uri(controlAddress);
using var listener = new TcpListener(IPAddress.Loopback, uri.Port);
using var stop = new CancellationTokenSource();
var pending = new List<Task>();
listener.Start();
try
{
    await AcceptRequestsAsync().ConfigureAwait(false);
}
finally
{
    listener.Stop();
    await stop.CancelAsync().ConfigureAwait(false);
    // The accept loop owns every request task and observes all failures before exit.
    await Task.WhenAll(pending).ConfigureAwait(false);
}
return 0;

async Task AcceptRequestsAsync()
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            // Every captured handler is joined in the top-level finally before owner disposal.
#pragma warning disable CA2025
            pending.Add(HandleAsync(client));
#pragma warning restore CA2025
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        // The shutdown response has completed before it cancels listener admission.
    }
}

async Task HandleAsync(TcpClient client)
{
    using (client)
    {
        var stream = client.GetStream();
        await using var streamAsyncDisposal = stream.ConfigureAwait(false);
        try
        {
            var request = await ReadRequestAsync(stream, stop.Token).ConfigureAwait(false);
            if (!string.Equals(request.Headers.GetValueOrDefault("X-SharpClaw-Control-Token"),
                token, StringComparison.Ordinal))
            {
                await WriteTextAsync(stream, 401, "Unauthorized", "bad token", "text/plain").ConfigureAwait(false);
                return;
            }

            await (request.Path switch
            {
                "/.sharpclaw/handshake" => RespondSharpclawHandshakeAsync(stream, request),
                "/.sharpclaw/discovery" => RespondSharpclawDiscoveryAsync(stream, request),
                "/.sharpclaw/health" => RespondSharpclawHealthAsync(stream, request),
                "/.sharpclaw/initialize" => RespondSharpclawInitializeAsync(stream, request),
                "/.sharpclaw/shutdown" => RespondSharpclawShutdownAsync(stream, request),
                "/.sharpclaw/tools/execute" => RespondSharpclawToolsExecuteAsync(stream, request),
                "/.sharpclaw/inline-tools/execute" => RespondSharpclawInlineToolsExecuteAsync(stream, request),
                "/.sharpclaw/tools/stream" => RespondSharpclawToolsStreamAsync(stream, request),
                "/.sharpclaw/contracts/invoke" => RespondSharpclawContractsInvokeAsync(stream, request),
                "/.sharpclaw/header-tags/resolve" => RespondSharpclawHeaderTagsResolveAsync(stream, request),
                "/.sharpclaw/resources/ids" => RespondSharpclawResourcesIdsAsync(stream, request),
                "/.sharpclaw/resources/lookup" => RespondSharpclawResourcesLookupAsync(stream, request),
                "/.sharpclaw/cli/execute" => RespondSharpclawCliExecuteAsync(stream, request),
                "/.sharpclaw/providers/models/list" => RespondSharpclawProvidersModelsListAsync(stream, request),
                "/.sharpclaw/providers/capabilities/resolve" => RespondSharpclawProvidersCapabilitiesResolveAsync(stream, request),
                "/.sharpclaw/providers/chat/complete" => RespondSharpclawProvidersChatCompleteAsync(stream, request),
                "/.sharpclaw/providers/chat/complete-tools" => RespondSharpclawProvidersChatCompleteToolsAsync(stream, request),
                "/.sharpclaw/providers/chat/stream-tools" => RespondSharpclawProvidersChatStreamToolsAsync(stream, request),
                "/.sharpclaw/providers/device-code/start" => RespondSharpclawProvidersDeviceCodeStartAsync(stream, request),
                "/.sharpclaw/providers/device-code/poll" => RespondSharpclawProvidersDeviceCodePollAsync(stream, request),
                "/.sharpclaw/providers/costs" => RespondSharpclawProvidersCostsAsync(stream, request),
                "/.sharpclaw/providers/agent-identifier-suffix" => RespondSharpclawProvidersAgentIdentifierSuffixAsync(stream, request),
                "/contributions/sample/ping" => RespondContributionsSamplePingAsync(stream, request),
                "/contributions/sample/echo" => RespondContributionsSampleEchoAsync(stream, request),
                "/contributions/sample/static/hello.txt" => RespondContributionsSampleStaticHelloTxtAsync(stream, request),
                "/contributions/sample/stream" => RespondContributionsSampleStreamAsync(stream, request),
                "/contributions/sample/ws" => RespondContributionsSampleWsAsync(stream, request),
                _ => RespondNotFoundAsync(stream, request),
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The listener owner cancels active request reads before joining all handlers.
        }
        catch (IOException) when (stop.IsCancellationRequested)
        {
            // Cancelled WebSocket reads may surface as transport failure during shutdown.
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested)
        {
            // WebSocket cancellation may dispose its transport during shutdown.
        }
    }
}

async Task RespondSharpclawHandshakeAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        protocolVersion = 1,
        SourceId,
        toolPrefix,
        runtime,
        runtimeVersion = "test-runtime",
        capabilities = ForeignSidecarProtocol.Capabilities,
    }).ConfigureAwait(false);
}

async Task RespondSharpclawDiscoveryAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        endpoints = DiscoveryEndpoints(),
        tools = DiscoveryTools(),
        inlineTools = DiscoveryInlineTools(),
        protocolContracts = DiscoveryProtocolContracts(),
        requiredProtocolContracts = DiscoveryRequiredProtocolContracts(),
        headerTags = DiscoveryHeaderTags(),
        resourceTypes = DiscoveryResourceTypes(),
        globalFlags = DiscoveryGlobalFlags(),
        uiContributions = DiscoveryUiContributions(),
        frontendContributions = DiscoveryFrontendContributions(),
        storageContracts = DiscoveryStorageContracts(),
        cliCommands = DiscoveryCliCommands(),
        providerPlugins = DiscoveryProviderPlugins(),
    }).ConfigureAwait(false);
}

object[] DiscoveryEndpoints() =>
    new[]
            {
                new
                {
                    method = "GET",
                    routePattern = "/contributions/sample/ping",
                    responseMode = "json",
                },
                new
                {
                    method = "POST",
                    routePattern = "/contributions/sample/echo",
                    responseMode = "json",
                },
                new
                {
                    method = "GET",
                    routePattern = "/contributions/sample/static/hello.txt",
                    responseMode = "static",
                },
                new
                {
                    method = "GET",
                    routePattern = "/contributions/sample/stream",
                    responseMode = "stream",
                },
                new
                {
                    method = "GET",
                    routePattern = "/contributions/sample/ws",
                    responseMode = "websocket",
                },
            };

object[] DiscoveryTools() =>
    new[]
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
            };

object[] DiscoveryInlineTools() =>
    new[]
            {
                new
                {
                    name = "sample_inline",
                    description = "Sample foreign inline tool.",
                    parametersSchema = EmptyObjectSchema(),
                },
            };

object[] DiscoveryProtocolContracts() =>
    new[]
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
            };

object[] DiscoveryRequiredProtocolContracts() =>
    new[]
            {
                new
                {
                    contractName = "theme_bridge",
                    optional = true,
                    description = "Optional theme bridge sample dependency.",
                },
            };

object[] DiscoveryHeaderTags() =>
    new[]
            {
                new
                {
                    name = "sample_header",
                    supportsContext = true,
                },
            };

object[] DiscoveryResourceTypes() =>
    new[]
            {
                new
                {
                    resourceType = "SampleResource",
                    grantLabel = "Sample Resource",
                    delegateMethodName = "AccessSampleResourceAsync",
                    defaultResourceKey = "sample",
                    supportsLookupItems = true,
                },
            };

object[] DiscoveryGlobalFlags() =>
    new[]
            {
                new
                {
                    flagKey = "CanUseSampleForeign",
                    displayName = "Use Sample Foreign",
                    description = "Use sample foreign module capabilities.",
                    delegateMethodName = "UseSampleForeignAsync",
                },
            };

object[] DiscoveryUiContributions() =>
    new[]
            {
                new
                {
                    contributionPoint = "settings_sidebar",
                    elementType = "button",
                    elementId = "sample-sidecar",
                    label = "Sample Sidecar",
                    actionToolName = "sample_job",
                },
            };

object[] DiscoveryFrontendContributions() =>
    new[]
            {
                new
                {
                    id = "sample.settings",
                    SourceId,
                    point = "SettingsPage",
                    builderKey = "sample-list",
                    label = "Sample Foreign",
                    RequiredOwnerId = SourceId,
                    order = 10,
                    list = new
                    {
                        listInternalApiPath = "/contributions/sample/ping",
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
            };

object[] DiscoveryStorageContracts() =>
    new[]
            {
                new
                {
                    SourceId,
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
            };

object[] DiscoveryCliCommands() =>
    new[]
            {
                new
                {
                    name = "sample",
                    aliases = ForeignSidecarProtocol.CliAliases,
                    scope = "TopLevel",
                    description = "Sample foreign CLI command.",
                    usageLines = ForeignSidecarProtocol.CliUsage,
                },
            };

object[] DiscoveryProviderPlugins() =>
    new[]
            {
                new
                {
                    providerKey = "sample-foreign-provider",
                    displayName = "Sample Foreign Provider",
                    OwnerId = SourceId,
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
                    parameterSpec = ProviderParameterSpec(),
                },
            };

static object ProviderParameterSpec() =>
    new
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
        validReasoningEffortValues = ForeignSidecarProtocol.ReasoningEfforts,
        supportsToolChoice = true,
        supportsStrictTools = true,
    };

async Task RespondSharpclawHealthAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        isHealthy = true,
        message = "ready",
    }).ConfigureAwait(false);
}

async Task RespondSharpclawInitializeAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        accepted = true,
        message = "initialized",
    }).ConfigureAwait(false);
}

async Task RespondSharpclawShutdownAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        accepted = true,
        message = "stopping",
    }).ConfigureAwait(false);
    if (!string.Equals(mode, "ignore-shutdown", StringComparison.Ordinal))
        await stop.CancelAsync().ConfigureAwait(false);
}

async Task RespondSharpclawToolsExecuteAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        result = BuildToolResult("job", request.Body),
    }).ConfigureAwait(false);
}

async Task RespondSharpclawInlineToolsExecuteAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        result = BuildToolResult("inline", request.Body),
    }).ConfigureAwait(false);
}

async Task RespondSharpclawToolsStreamAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteNdjsonAsync(
        stream,
        new { delta = "first:" },
        new { delta = "second" },
        new { isFinal = true }).ConfigureAwait(false);
}

async Task RespondSharpclawContractsInvokeAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        result = BuildContractResult(request.Body),
    }).ConfigureAwait(false);
}

async Task RespondSharpclawHeaderTagsResolveAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        value = BuildHeaderTagResult(request.Body),
    }).ConfigureAwait(false);
}

async Task RespondSharpclawResourcesIdsAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        ids = new[]
        {
            new Guid(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11),
        },
    }).ConfigureAwait(false);
}

async Task RespondSharpclawResourcesLookupAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        items = new[]
        {
            new
            {
                id = new Guid(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11),
                name = "Sample One",
            },
        },
    }).ConfigureAwait(false);
}

async Task RespondSharpclawCliExecuteAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        success = true,
        stdout = BuildCliResult(request.Body),
        stderr = "",
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersModelsListAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        modelIds = ForeignSidecarProtocol.ModelIds,
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersCapabilitiesResolveAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        tags = ForeignSidecarProtocol.ModelCapabilities,
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersChatCompleteAsync(NetworkStream stream, SidecarRequest request)
{
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
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersChatCompleteToolsAsync(NetworkStream stream, SidecarRequest request)
{
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
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersChatStreamToolsAsync(NetworkStream stream, SidecarRequest request)
{
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
        }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersDeviceCodeStartAsync(NetworkStream stream, SidecarRequest request)
{
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
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersDeviceCodePollAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        accessToken = "device-access-token",
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersCostsAsync(NetworkStream stream, SidecarRequest request)
{
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
                    start = DateTimeOffset.Parse("2026-05-01T00:00:00Z", CultureInfo.InvariantCulture),
                    end = DateTimeOffset.Parse("2026-05-02T00:00:00Z", CultureInfo.InvariantCulture),
                    amount = 12.34m,
                },
            },
        },
    }).ConfigureAwait(false);
}

async Task RespondSharpclawProvidersAgentIdentifierSuffixAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        suffix = "sample-sidecar",
    }).ConfigureAwait(false);
}

async Task RespondContributionsSamplePingAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        ok = true,
        path = request.Path,
        query = request.Query,
        marker = request.Headers.GetValueOrDefault("X-Test-Marker"),
    }, ("X-Sidecar", "yes")).ConfigureAwait(false);
}

async Task RespondContributionsSampleEchoAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteJsonAsync(stream, new
    {
        method = request.Method,
        path = request.Path,
        query = request.Query,
        body = request.Body,
        contentType = request.Headers.GetValueOrDefault("Content-Type"),
    }).ConfigureAwait(false);
}

async Task RespondContributionsSampleStaticHelloTxtAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteTextAsync(
        stream,
        200,
        "OK",
        "static-parity-asset",
        "text/plain").ConfigureAwait(false);
}

async Task RespondContributionsSampleStreamAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteNdjsonAsync(
        stream,
        new { delta = "first:" },
        new { delta = "second" },
        new { isFinal = true }).ConfigureAwait(false);
}

async Task RespondContributionsSampleWsAsync(NetworkStream stream, SidecarRequest request)
{
    await HandleWebSocketEchoAsync(stream, request, stop.Token).ConfigureAwait(false);
}

async Task RespondNotFoundAsync(NetworkStream stream, SidecarRequest request)
{
    await WriteTextAsync(stream, 404, "Not Found", "not found", "text/plain").ConfigureAwait(false);
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
    return $"cli:{command}:{string.Join(',', args)}";
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

static async Task HandleWebSocketEchoAsync(NetworkStream stream, SidecarRequest request, CancellationToken cancellationToken)
{
    if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key))
    {
        await WriteTextAsync(stream, 400, "Bad Request", "WebSocket connections only.", "text/plain").ConfigureAwait(false);
        return;
    }

    // RFC6455 sections4.2.2 and10.8 mandate this non-security handshake digest.
#pragma warning disable CA5350
    var accept = Convert.ToBase64String(SHA1.HashData(
        Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
    var headers = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        $"Sec-WebSocket-Accept: {accept}\r\n" +
        "\r\n");
    await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);

    using var socket = WebSocket.CreateFromStream(
        stream,
        isServer: true,
        subProtocol: null,
        keepAliveInterval: TimeSpan.FromSeconds(30));
    var buffer = new byte[16 * 1024];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "closing",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var response = Encoding.UTF8.GetBytes("sidecar:" + text);
        await socket.SendAsync(
            response,
            result.MessageType,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }
}

static async Task<SidecarRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
{
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
    var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var method = parts.Length >= 1 ? parts[0] : "GET";
    var requestUri = parts.Length >= 2 ? new Uri("http://127.0.0.1" + parts[1]) : new Uri("http://127.0.0.1/");
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line && line.Length > 0)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            continue;

        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }

    var body = string.Empty;
    if (headers.TryGetValue("Content-Length", out var contentLengthText)
        && int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength)
        && contentLength > 0)
    {
        var buffer = new char[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD103", Justification = "Serializes this bounded fixture response to text before the awaited asynchronous network write; there is no stream serialization operation here.")]
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
        "application/x-ndjson").ConfigureAwait(false);
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
    await stream.WriteAsync(headers, CancellationToken.None).ConfigureAwait(false);
    await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
}
