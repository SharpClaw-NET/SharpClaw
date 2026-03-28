using System.Text;
using System.Text.Json;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Telegram bot using long-polling against
/// the Telegram Bot API. Receives incoming messages and forwards them to
/// the SharpClaw core via <see cref="InternalApiClient"/>.
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires (e.g. after bot settings are changed via the gateway).
/// </para>
/// </summary>
public sealed class TelegramBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;

    private Guid? _defaultChannelId;
    private Guid? _defaultThreadId;

    private const string TelegramApiBase = "https://api.telegram.org/bot";

    public TelegramBotService(
        InternalApiClient coreApi,
        ILogger<TelegramBotService> logger,
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

        // Outer loop: fetch config, run poll loop, restart on reload signal
        while (!stoppingToken.IsCancellationRequested)
        {
            BotConfigResponse? config;
            try
            {
                config = await _coreApi.GetAsync<BotConfigResponse>(
                    "/bots/config/telegram", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Telegram bot config from core API. Retrying on reload...");
                config = null;
            }

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Telegram bot is enabled but no BotToken is configured.");
                else
                    _logger.LogInformation(
                        "Telegram bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _defaultChannelId = config.DefaultChannelId;
            _defaultThreadId = config.DefaultThreadId;

            _logger.LogInformation("Telegram bot starting with long-polling...");

            // Create an HttpClient for this config cycle
            var client = _httpClientFactory.CreateClient("TelegramBot");
            client.BaseAddress = new Uri($"{TelegramApiBase}{config.BotToken}/");
            client.Timeout = TimeSpan.FromSeconds(35);

            if (!await ValidateBotTokenAsync(client, stoppingToken))
            {
                _logger.LogError(
                    "Telegram bot token validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Inner loop: poll until cancelled or reload signal fires
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var reloadTask = WaitForReloadAsync(pollCts);

            long offset = 0;
            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    var updates = await PollUpdatesAsync(client, offset, pollCts.Token);
                    if (updates is not null)
                    {
                        foreach (var update in updates)
                        {
                            offset = update.UpdateId + 1;
                            await HandleUpdateAsync(client, update, pollCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (pollCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Telegram polling error. Retrying in 5 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // If we exited because of reload (not app shutdown), log and loop
            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Telegram bot reloading configuration...");

            await reloadTask;
        }

        _logger.LogInformation("Telegram bot stopped.");
    }

    /// <summary>
    /// Waits for a reload signal and then cancels the poll CTS so the inner loop exits.
    /// </summary>
    private async Task WaitForReloadAsync(CancellationTokenSource pollCts)
    {
        try
        {
            await _reloadSignal.WaitAsync(pollCts.Token);
            // Reload was signalled — cancel the poll loop
            await pollCts.CancelAsync();
        }
        catch (OperationCanceledException)
        {
            // App shutdown or poll loop already ended — nothing to do
        }
    }

    private async Task<bool> ValidateBotTokenAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync("getMe", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Telegram getMe returned {Status}.", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Telegram bot authenticated: {Response}", json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Telegram bot token.");
            return false;
        }
    }

    private async Task<TelegramUpdate[]?> PollUpdatesAsync(
        HttpClient client, long offset, CancellationToken ct)
    {
        var url = $"getUpdates?offset={offset}&timeout=30&allowed_updates=[\"message\"]";
        var response = await client.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telegram getUpdates returned {Status}.", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<TelegramApiResponse<TelegramUpdate[]>>(
            json, JsonOptions);
        return result is { Ok: true } ? result.Result : null;
    }

    private async Task HandleUpdateAsync(
        HttpClient client, TelegramUpdate update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message)
            return;

        // Only handle direct messages (private chats)
        if (message.Chat.Type != "private")
            return;

        var chatId = message.Chat.Id;
        var username = message.From?.Username;
        var displayName = message.From?.FirstName;
        var fromLabel = username ?? displayName ?? "unknown";

        _logger.LogInformation(
            "Telegram DM from @{User} in chat {ChatId}: {Text}",
            fromLabel, chatId, text);

        if (_defaultChannelId is null)
        {
            await SendMessageAsync(client, chatId,
                "⚠ No default channel configured for this bot.", ct);
            return;
        }

        try
        {
            var chatRequest = new TelegramChatRequest(
                text, null, "Telegram", username, displayName);

            // Route to thread-scoped endpoint when a thread is configured
            var chatPath = _defaultThreadId is not null
                ? $"/channels/{_defaultChannelId}/chat/threads/{_defaultThreadId}"
                : $"/channels/{_defaultChannelId}/chat";

            var response = await _coreApi
                .PostAsync<TelegramChatRequest, TelegramChatResponse>(
                    chatPath, chatRequest, ct);

            var reply = response?.AssistantMessage?.Content ?? "No response from agent.";
            await SendMessageAsync(client, chatId, reply, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relay Telegram message to core.");
            await SendMessageAsync(client, chatId,
                "⚠ Failed to process message.", ct);
        }
    }

    private async Task SendMessageAsync(
        HttpClient client, long chatId, string text, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            new { chat_id = chatId, text }, JsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            await client.PostAsync("sendMessage", content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send Telegram message to chat {ChatId}.", chatId);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    // ── Minimal Telegram API models ──────────────────────────────

    private sealed record TelegramApiResponse<T>
    {
        public bool Ok { get; init; }
        public T? Result { get; init; }
    }

    private sealed record TelegramUpdate
    {
        public long UpdateId { get; init; }
        public TelegramMessage? Message { get; init; }
    }

    private sealed record TelegramMessage
    {
        public long MessageId { get; init; }
        public TelegramChat Chat { get; init; } = default!;
        public TelegramUser? From { get; init; }
        public string? Text { get; init; }
    }

    private sealed record TelegramChat
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public string? Type { get; init; }
    }

    private sealed record TelegramUser
    {
        public long Id { get; init; }
        public string? FirstName { get; init; }
        public string? Username { get; init; }
    }

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }

    // ── Core API chat DTOs ───────────────────────────────────────

    private sealed record TelegramChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record TelegramChatResponse
    {
        public TelegramChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record TelegramChatMessageDto
    {
        public string? Content { get; init; }
    }
}
