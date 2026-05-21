using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules.Foreign;

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
                ForeignModuleAgentJobContext.From(job)),
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
                ForeignModuleInlineToolContext.From(context)),
            ct);

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
                    ForeignModuleAgentJobContext.From(job)),
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
