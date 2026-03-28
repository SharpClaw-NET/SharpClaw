using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Discord bot using the Gateway WebSocket
/// API (v10). Receives incoming messages and will eventually forward
/// them to the SharpClaw core via <c>InternalApiClient</c>.
/// <para>
/// Bot configuration (enabled flag and token) is fetched from the core
/// database via <see cref="InternalApiClient"/> at startup.
/// </para>
/// </summary>
public sealed class DiscordBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private const string DiscordGatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    private const string DiscordApiBase = "https://discord.com/api/v10/";

    // Gateway intents: GUILDS (1 << 0) | GUILD_MESSAGES (1 << 9) | MESSAGE_CONTENT (1 << 15)
    private const int GatewayIntents = (1 << 0) | (1 << 9) | (1 << 15);

    public DiscordBotService(
        InternalApiClient coreApi,
        ILogger<DiscordBotService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _coreApi = coreApi;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the core API to be ready after startup
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        BotConfigResponse? config;
        try
        {
            config = await _coreApi.GetAsync<BotConfigResponse>("/bots/config/discord", stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Discord bot config from core API. Stopping.");
            return;
        }

        if (config is null || !config.Enabled)
        {
            _logger.LogInformation("Discord bot is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            _logger.LogWarning("Discord bot is enabled but no BotToken is configured. Stopping.");
            return;
        }

        _logger.LogInformation("Discord bot starting...");

        // Validate token via REST before connecting to the gateway
        if (!await ValidateBotTokenAsync(config.BotToken, stoppingToken))
        {
            _logger.LogError("Discord bot token validation failed. Stopping.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunGatewaySessionAsync(config.BotToken, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord gateway session failed. Reconnecting in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Discord bot stopped.");
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
                _logger.LogWarning("Discord gateway closed: {Status} {Description}.",
                    ws.CloseStatus, ws.CloseStatusDescription);
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

    private Task HandleDispatchAsync(
        ClientWebSocket ws, string token, GatewayPayload payload, CancellationToken ct)
    {
        switch (payload.T)
        {
            case "READY":
                _logger.LogInformation("Discord READY event received.");
                break;

            case "MESSAGE_CREATE":
                HandleMessageCreate(payload);
                break;

            default:
                _logger.LogDebug("Discord dispatch: {EventName}.", payload.T);
                break;
        }

        return Task.CompletedTask;
    }

    private void HandleMessageCreate(GatewayPayload payload)
    {
        if (payload.D is not { } data) return;

        var content = data.TryGetProperty("content", out var c) ? c.GetString() : null;
        var authorName = data.TryGetProperty("author", out var author)
            && author.TryGetProperty("username", out var u)
                ? u.GetString()
                : "unknown";
        var channelId = data.TryGetProperty("channel_id", out var ch)
            ? ch.GetString()
            : "unknown";
        var isBot = data.TryGetProperty("author", out var a2)
            && a2.TryGetProperty("bot", out var b) && b.GetBoolean();

        if (isBot) return; // Ignore messages from other bots (and ourselves)

        _logger.LogInformation(
            "Discord message from @{User} in channel {ChannelId}: {Content}",
            authorName, channelId, content);

        // TODO: Forward to SharpClaw core via InternalApiClient and return the response.
        // Will need to POST to the Discord channel via REST to reply.
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
    }
}
