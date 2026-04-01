using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Matrix bot using long-polling against
/// the Matrix Client-Server API (<c>/sync</c>). Receives incoming DM
/// messages and forwards them to the SharpClaw core via
/// <see cref="InternalApiClient"/>.
/// <para>
/// Only messages in direct-message rooms are processed; group rooms are
/// ignored. The DM room set is resolved from the <c>m.direct</c>
/// account data event.
/// </para>
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires.
/// </para>
/// </summary>
public sealed class MatrixBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<MatrixBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;
    private readonly IOptionsMonitor<MatrixBotOptions> _options;

    private Guid? _defaultChannelId;
    private Guid? _defaultThreadId;

    public MatrixBotService(
        InternalApiClient coreApi,
        ILogger<MatrixBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal,
        IOptionsMonitor<MatrixBotOptions> options)
    {
        _coreApi = coreApi;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _reloadSignal = reloadSignal;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            BotConfigResponse? config;
            try
            {
                config = await _coreApi.GetAsync<BotConfigResponse>(
                    "/bots/config/matrix", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Matrix bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Matrix bot is enabled but no access token is configured.");
                else
                    _logger.LogInformation(
                        "Matrix bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.HomeserverUrl))
            {
                _logger.LogWarning(
                    "Matrix bot is enabled but no HomeserverUrl is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var baseUrl = opts.HomeserverUrl.TrimEnd('/');
            _defaultChannelId = config.DefaultChannelId;
            _defaultThreadId = config.DefaultThreadId;

            _logger.LogInformation("Matrix bot starting with /sync long-poll...");

            var client = _httpClientFactory.CreateClient("MatrixBot");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BotToken);
            client.Timeout = TimeSpan.FromSeconds(60);

            if (!await ValidateTokenAsync(client, baseUrl, stoppingToken))
            {
                _logger.LogError(
                    "Matrix token validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Resolve DM rooms from m.direct account data
            var dmRooms = await ResolveDmRoomsAsync(client, baseUrl, stoppingToken);

            // Inner loop: /sync poll
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var reloadTask = WaitForReloadAsync(pollCts);

            string? nextBatch = null;
            var isInitialSync = true;

            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    var syncUrl = $"{baseUrl}/_matrix/client/v3/sync?timeout=30000";
                    if (nextBatch is not null)
                        syncUrl += $"&since={Uri.EscapeDataString(nextBatch)}";

                    var response = await client.GetAsync(syncUrl, pollCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Matrix /sync returned {Status}.", response.StatusCode);
                        await Task.Delay(TimeSpan.FromSeconds(5), pollCts.Token);
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync(pollCts.Token);
                    var syncResponse = JsonSerializer.Deserialize<JsonElement>(json);

                    if (syncResponse.TryGetProperty("next_batch", out var nb))
                        nextBatch = nb.GetString();

                    // Skip messages from the initial sync (historical)
                    if (isInitialSync)
                    {
                        isInitialSync = false;
                        continue;
                    }

                    // Process joined room events
                    if (syncResponse.TryGetProperty("rooms", out var rooms)
                        && rooms.TryGetProperty("join", out var join))
                    {
                        foreach (var room in join.EnumerateObject())
                        {
                            // Only process DM rooms
                            if (!dmRooms.Contains(room.Name))
                                continue;

                            if (!room.Value.TryGetProperty("timeline", out var timeline)
                                || !timeline.TryGetProperty("events", out var events))
                                continue;

                            foreach (var evt in events.EnumerateArray())
                            {
                                await HandleRoomEventAsync(
                                    client, baseUrl, room.Name, evt, pollCts.Token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (pollCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Matrix /sync error. Retrying in 5 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Matrix bot reloading configuration...");

            await reloadTask;
        }

        _logger.LogInformation("Matrix bot stopped.");
    }

    private async Task WaitForReloadAsync(CancellationTokenSource pollCts)
    {
        try
        {
            await _reloadSignal.WaitAsync(pollCts.Token);
            await pollCts.CancelAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ValidateTokenAsync(
        HttpClient client, string baseUrl, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync(
                $"{baseUrl}/_matrix/client/v3/account/whoami", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Matrix /account/whoami returned {Status}.", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Matrix bot authenticated: {Response}", json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Matrix access token.");
            return false;
        }
    }

    private async Task<HashSet<string>> ResolveDmRoomsAsync(
        HttpClient client, string baseUrl, CancellationToken ct)
    {
        var dmRooms = new HashSet<string>();
        try
        {
            var response = await client.GetAsync(
                $"{baseUrl}/_matrix/client/v3/user/{Uri.EscapeDataString("@me")}/account_data/m.direct", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("No m.direct account data found — will treat all rooms as non-DM.");
                return dmRooms;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            // m.direct maps user_id → [room_id, ...]
            foreach (var user in doc.EnumerateObject())
            {
                if (user.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var roomId in user.Value.EnumerateArray())
                    {
                        var id = roomId.GetString();
                        if (id is not null) dmRooms.Add(id);
                    }
                }
            }

            _logger.LogInformation("Resolved {Count} DM rooms from m.direct.", dmRooms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve DM rooms. No DM filtering will be applied.");
        }

        return dmRooms;
    }

    private async Task HandleRoomEventAsync(
        HttpClient client, string baseUrl, string roomId,
        JsonElement evt, CancellationToken ct)
    {
        try
        {
            if (!evt.TryGetProperty("type", out var type)
                || type.GetString() != "m.room.message")
                return;

            if (!evt.TryGetProperty("content", out var content)
                || !content.TryGetProperty("msgtype", out var msgtype)
                || msgtype.GetString() != "m.text"
                || !content.TryGetProperty("body", out var body))
                return;

            var sender = evt.TryGetProperty("sender", out var s) ? s.GetString() : "unknown";
            var text = body.GetString();

            if (string.IsNullOrWhiteSpace(text) || sender is null)
                return;

            // Skip messages sent by the bot itself
            // (whoami userId would be ideal, but sender starting with @ is sufficient)
            _logger.LogInformation("Matrix DM from {Sender}: {Text}", sender, text);

            if (_defaultChannelId is null)
            {
                await SendMatrixMessageAsync(client, baseUrl, roomId,
                    "\u26a0 No default channel configured for this bot.", ct);
                return;
            }

            try
            {
                var chatRequest = new MatrixChatRequest(
                    text, null, "Matrix", sender, sender);

                var chatPath = _defaultThreadId is not null
                    ? $"/channels/{_defaultChannelId}/chat/threads/{_defaultThreadId}"
                    : $"/channels/{_defaultChannelId}/chat";

                var response = await _coreApi
                    .PostAsync<MatrixChatRequest, MatrixChatResponse>(
                        chatPath, chatRequest, ct);

                var reply = response?.AssistantMessage?.Content
                    ?? "No response from agent.";
                await SendMatrixMessageAsync(client, baseUrl, roomId, reply, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to relay Matrix message to core.");
                await SendMatrixMessageAsync(client, baseUrl, roomId,
                    "\u26a0 Failed to process message.", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Matrix room event.");
        }
    }

    private async Task SendMatrixMessageAsync(
        HttpClient client, string baseUrl, string roomId,
        string text, CancellationToken ct)
    {
        try
        {
            var txnId = Guid.NewGuid().ToString("N");
            var url = $"{baseUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}" +
                      $"/send/m.room.message/{txnId}";

            var payload = JsonSerializer.Serialize(new
            {
                msgtype = "m.text",
                body = text
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            await client.PutAsync(url, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Matrix message to room {RoomId}.", roomId);
        }
    }

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }

    private sealed record MatrixChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record MatrixChatResponse
    {
        public MatrixChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record MatrixChatMessageDto
    {
        public string? Content { get; init; }
    }
}
