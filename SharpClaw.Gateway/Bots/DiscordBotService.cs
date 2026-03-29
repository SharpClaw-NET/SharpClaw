using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Discord bot using the Gateway WebSocket
/// API (v10). Receives incoming messages and forwards them to the
/// SharpClaw core via <see cref="InternalApiClient"/>.
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires (e.g. after bot settings are changed via the gateway).
/// </para>
/// </summary>
public sealed class DiscordBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;

    private Guid? _defaultChannelId;
    private Guid? _defaultThreadId;
    private string? _botToken;

    private const string DiscordGatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    private const string DiscordApiBase = "https://discord.com/api/v10/";

    // Gateway intents: GUILDS (1<<0) | GUILD_MESSAGES (1<<9) | DIRECT_MESSAGES (1<<12)
    // MESSAGE_CONTENT (1<<15) is privileged and NOT needed for DMs — Discord
    // always delivers message content in direct messages with the bot.
    private const int GatewayIntents = (1 << 0) | (1 << 9) | (1 << 12);

    // Discord close codes that indicate a configuration error (non-recoverable by reconnecting)
    private static readonly HashSet<int> FatalCloseCodes = [4003, 4004, 4010, 4011, 4012, 4013, 4014];

    public DiscordBotService(
        InternalApiClient coreApi,
        ILogger<DiscordBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal)
    {
        _coreApi = coreApi;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _reloadSignal = reloadSignal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the core API to be ready after startup
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        // Outer loop: fetch config, run gateway session, restart on reload signal
        while (!stoppingToken.IsCancellationRequested)
        {
            BotConfigResponse? config;
            try
            {
                config = await _coreApi.GetAsync<BotConfigResponse>(
                    "/bots/config/discord", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Discord bot config from core API. Retrying on reload...");
                config = null;
            }

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Discord bot is enabled but no BotToken is configured.");
                else
                    _logger.LogInformation(
                        "Discord bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _defaultChannelId = config.DefaultChannelId;
            _defaultThreadId = config.DefaultThreadId;
            _botToken = config.BotToken;

            _logger.LogInformation("Discord bot starting...");

            if (!await ValidateBotTokenAsync(config.BotToken, stoppingToken))
            {
                _logger.LogError(
                    "Discord bot token validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Inner loop: run gateway session, restart on error or reload
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var reloadTask = WaitForReloadAsync(sessionCts);

            while (!sessionCts.Token.IsCancellationRequested)
            {
                try
                {
                    await RunGatewaySessionAsync(config.BotToken, sessionCts.Token);
                }
                catch (OperationCanceledException) when (sessionCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (DiscordFatalCloseException ex)
                {
                    _logger.LogError(
                        "Discord gateway fatal error (code {Code}: {Reason}). " +
                        "Check the Discord Developer Portal → Bot → Privileged Gateway Intents. " +
                        "Waiting for reload signal...",
                        ex.CloseCode, ex.Reason);
                    break; // exit inner loop → awaits reload signal
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Discord gateway session failed. Reconnecting in 5 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), sessionCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Discord bot reloading configuration...");

            await reloadTask;
        }

        _logger.LogInformation("Discord bot stopped.");
    }

    /// <summary>
    /// Waits for a reload signal and then cancels the session CTS so the inner loop exits.
    /// </summary>
    private async Task WaitForReloadAsync(CancellationTokenSource sessionCts)
    {
        try
        {
            await _reloadSignal.WaitAsync(sessionCts.Token);
            await sessionCts.CancelAsync();
        }
        catch (OperationCanceledException)
        {
            // App shutdown or session already ended — nothing to do
        }
    }

    private async Task<bool> ValidateBotTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("DiscordBot");
            client.BaseAddress = new Uri(DiscordApiBase);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

            var response = await client.GetAsync("users/@me", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Discord /users/@me returned {Status}.", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Discord bot authenticated: {Response}", json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Discord bot token.");
            return false;
        }
    }

    private async Task RunGatewaySessionAsync(string token, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(DiscordGatewayUrl), ct);
        _logger.LogInformation("Connected to Discord gateway.");

        var buffer = new byte[16384];
        int? heartbeatInterval = null;
        int? lastSequence = null;
        Task? heartbeatTask = null;
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var closeCode = (int?)ws.CloseStatus;
                _logger.LogWarning("Discord gateway closed: {Code} {Description}.",
                    closeCode, ws.CloseStatusDescription);

                if (closeCode.HasValue && FatalCloseCodes.Contains(closeCode.Value))
                    throw new DiscordFatalCloseException(closeCode.Value,
                        ws.CloseStatusDescription ?? "unknown");

                break;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var payload = JsonSerializer.Deserialize<GatewayPayload>(json, JsonOptions);
            if (payload is null) continue;

            if (payload.S.HasValue)
                lastSequence = payload.S.Value;

            switch (payload.Op)
            {
                case 10: // Hello
                    heartbeatInterval = payload.D?.GetProperty("heartbeat_interval").GetInt32();
                    _logger.LogInformation("Discord Hello — heartbeat interval {Interval}ms.", heartbeatInterval);

                    // Start heartbeating
                    heartbeatTask = HeartbeatLoopAsync(
                        ws, heartbeatInterval!.Value, () => lastSequence, heartbeatCts.Token);

                    // Send Identify
                    await SendIdentifyAsync(ws, token, ct);
                    break;

                case 11: // Heartbeat ACK
                    break;

                case 0: // Dispatch
                    await HandleDispatchAsync(ws, token, payload, ct);
                    break;

                case 7: // Reconnect
                    _logger.LogWarning("Discord requested reconnect.");
                    return; // outer loop will reconnect

                case 9: // Invalid Session
                    _logger.LogWarning("Discord invalid session. Reconnecting...");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    return;

                default:
                    _logger.LogDebug("Discord gateway op {Op}.", payload.Op);
                    break;
            }
        }

        await heartbeatCts.CancelAsync();
        if (heartbeatTask is not null)
        {
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task SendIdentifyAsync(ClientWebSocket ws, string token, CancellationToken ct)
    {
        var identify = new
        {
            op = 2,
            d = new
            {
                token,
                intents = GatewayIntents,
                properties = new
                {
                    os = Environment.OSVersion.Platform.ToString(),
                    browser = "SharpClaw.Gateway",
                    device = "SharpClaw.Gateway"
                }
            }
        };

        var json = JsonSerializer.Serialize(identify, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        _logger.LogInformation("Discord Identify sent.");
    }

    private static async Task HeartbeatLoopAsync(
        ClientWebSocket ws, int intervalMs, Func<int?> getSequence, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(intervalMs, ct);

            var heartbeat = JsonSerializer.Serialize(new { op = 1, d = getSequence() });
            var bytes = Encoding.UTF8.GetBytes(heartbeat);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task HandleDispatchAsync(
        ClientWebSocket ws, string token, GatewayPayload payload, CancellationToken ct)
    {
        switch (payload.T)
        {
            case "READY":
                _logger.LogInformation("Discord READY event received.");
                break;

            case "MESSAGE_CREATE":
                await HandleMessageCreateAsync(payload, ct);
                break;

            default:
                _logger.LogDebug("Discord dispatch: {EventName}.", payload.T);
                break;
        }
    }

    private async Task HandleMessageCreateAsync(GatewayPayload payload, CancellationToken ct)
    {
        if (payload.D is not { } data) return;

        var content = data.TryGetProperty("content", out var c) ? c.GetString() : null;
        var authorName = data.TryGetProperty("author", out var author)
            && author.TryGetProperty("username", out var u)
                ? u.GetString()
                : "unknown";
        var discordChannelId = data.TryGetProperty("channel_id", out var ch)
            ? ch.GetString()
            : null;
        var isBot = data.TryGetProperty("author", out var a2)
            && a2.TryGetProperty("bot", out var b) && b.GetBoolean();

        // Only handle direct messages (DMs have no guild_id)
        var isGuildMessage = data.TryGetProperty("guild_id", out _);

        if (isBot || isGuildMessage || string.IsNullOrWhiteSpace(content) || discordChannelId is null)
            return;

        _logger.LogInformation(
            "Discord DM from @{User} in channel {ChannelId}: {Content}",
            authorName, discordChannelId, content);

        if (_defaultChannelId is null)
        {
            await SendDiscordMessageAsync(discordChannelId,
                "\u26a0 No default channel configured for this bot.", ct);
            return;
        }

        try
        {
            var chatRequest = new DiscordChatRequest(
                content, null, "Discord", authorName, authorName);

            var chatPath = _defaultThreadId is not null
                ? $"/channels/{_defaultChannelId}/chat/threads/{_defaultThreadId}"
                : $"/channels/{_defaultChannelId}/chat";

            var response = await _coreApi
                .PostAsync<DiscordChatRequest, DiscordChatResponse>(
                    chatPath, chatRequest, ct);

            var reply = response?.AssistantMessage?.Content ?? "No response from agent.";
            await SendDiscordMessageAsync(discordChannelId, reply, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relay Discord message to core.");
            await SendDiscordMessageAsync(discordChannelId,
                "\u26a0 Failed to process message.", ct);
        }
    }

    private async Task SendDiscordMessageAsync(
        string channelId, string text, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("DiscordBot");
            client.BaseAddress = new Uri(DiscordApiBase);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bot", _botToken);

            // Discord has a 2000-character message limit
            if (text.Length > 2000)
                text = text[..1997] + "...";

            var payload = JsonSerializer.Serialize(
                new { content = text }, JsonOptions);
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");

            await client.PostAsync($"channels/{channelId}/messages", body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send Discord message to channel {ChannelId}.", channelId);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    // ── Minimal Discord Gateway models ───────────────────────────

    private sealed record GatewayPayload
    {
        public int Op { get; init; }
        public JsonElement? D { get; init; }
        public int? S { get; init; }
        public string? T { get; init; }
    }

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }

    // ── Core API chat DTOs ───────────────────────────────────────

    private sealed record DiscordChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record DiscordChatResponse
    {
        public DiscordChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record DiscordChatMessageDto
    {
        public string? Content { get; init; }
    }

    private sealed class DiscordFatalCloseException(int code, string reason)
        : Exception($"Discord fatal close {code}: {reason}")
    {
        public int CloseCode => code;
        public string Reason => reason;
    }
}
