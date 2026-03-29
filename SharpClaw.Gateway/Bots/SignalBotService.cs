using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Signal bot by polling the signal-cli REST
/// API (<c>GET /v1/receive/{number}</c>). Receives incoming DMs and
/// forwards them to the SharpClaw core via <see cref="InternalApiClient"/>.
/// <para>
/// Only individual (non-group) messages are processed; group messages
/// (those with a <c>groupInfo</c> object) are ignored.
/// </para>
/// <para>
/// Requires a running <c>signal-cli</c> REST API instance with a
/// registered phone number.
/// </para>
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires.
/// </para>
/// </summary>
public sealed class SignalBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<SignalBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BotReloadSignal _reloadSignal;
    private readonly IOptionsMonitor<SignalBotOptions> _options;

    private Guid? _defaultChannelId;
    private Guid? _defaultThreadId;

    public SignalBotService(
        InternalApiClient coreApi,
        ILogger<SignalBotService> logger,
        IHttpClientFactory httpClientFactory,
        BotReloadSignal reloadSignal,
        IOptionsMonitor<SignalBotOptions> options)
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
                    "/bots/config/signal", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Signal bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled)
            {
                _logger.LogInformation(
                    "Signal bot is disabled or not configured. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.ApiUrl))
            {
                _logger.LogWarning(
                    "Signal bot is enabled but no ApiUrl is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.PhoneNumber))
            {
                _logger.LogWarning(
                    "Signal bot is enabled but no PhoneNumber is configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var apiUrl = opts.ApiUrl.TrimEnd('/');
            var phone = Uri.EscapeDataString(opts.PhoneNumber);
            _defaultChannelId = config.DefaultChannelId;
            _defaultThreadId = config.DefaultThreadId;

            _logger.LogInformation("Signal bot starting with poll loop...");

            var client = _httpClientFactory.CreateClient("SignalBot");
            client.Timeout = TimeSpan.FromSeconds(35);

            if (!await ValidateSignalApiAsync(client, apiUrl, stoppingToken))
            {
                _logger.LogError(
                    "Signal API validation failed. Waiting for reload signal...");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Inner loop: poll
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var reloadTask = WaitForReloadAsync(pollCts);

            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    var messages = await PollMessagesAsync(client, apiUrl, phone, pollCts.Token);
                    if (messages is not null)
                    {
                        foreach (var msg in messages)
                        {
                            await HandleMessageAsync(client, apiUrl, phone, msg, pollCts.Token);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), pollCts.Token);
                }
                catch (OperationCanceledException) when (pollCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Signal polling error. Retrying in 5 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Signal bot reloading configuration...");

            await reloadTask;
        }

        _logger.LogInformation("Signal bot stopped.");
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

    private async Task<bool> ValidateSignalApiAsync(
        HttpClient client, string apiUrl, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync($"{apiUrl}/v1/about", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Signal API /v1/about returned {Status}.", response.StatusCode);
                return false;
            }

            _logger.LogInformation("Signal API validated.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Signal API at {Url}.", apiUrl);
            return false;
        }
    }

    private static async Task<JsonElement[]?> PollMessagesAsync(
        HttpClient client, string apiUrl, string phone, CancellationToken ct)
    {
        var response = await client.GetAsync(
            $"{apiUrl}/v1/receive/{phone}", ct);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var array = JsonSerializer.Deserialize<JsonElement[]>(json);
        return array;
    }

    private async Task HandleMessageAsync(
        HttpClient client, string apiUrl, string phone,
        JsonElement msg, CancellationToken ct)
    {
        try
        {
            if (!msg.TryGetProperty("envelope", out var envelope))
                return;

            // Skip group messages — only handle DMs
            if (envelope.TryGetProperty("dataMessage", out var dataMsg))
            {
                if (dataMsg.TryGetProperty("groupInfo", out _))
                    return;

                var text = dataMsg.TryGetProperty("message", out var m)
                    ? m.GetString() : null;

                if (string.IsNullOrWhiteSpace(text))
                    return;

                var from = envelope.TryGetProperty("source", out var src)
                    ? src.GetString() ?? "unknown" : "unknown";
                var displayName = envelope.TryGetProperty("sourceName", out var sn)
                    ? sn.GetString() ?? from : from;

                _logger.LogInformation(
                    "Signal DM from {From} ({Name}): {Text}", from, displayName, text);

                if (_defaultChannelId is null)
                {
                    await SendSignalMessageAsync(client, apiUrl, phone, from,
                        "\u26a0 No default channel configured for this bot.", ct);
                    return;
                }

                try
                {
                    var chatRequest = new SignalChatRequest(
                        text, null, "Signal", from, displayName);

                    var chatPath = _defaultThreadId is not null
                        ? $"/channels/{_defaultChannelId}/chat/threads/{_defaultThreadId}"
                        : $"/channels/{_defaultChannelId}/chat";

                    var response = await _coreApi
                        .PostAsync<SignalChatRequest, SignalChatResponse>(
                            chatPath, chatRequest, ct);

                    var reply = response?.AssistantMessage?.Content
                        ?? "No response from agent.";
                    await SendSignalMessageAsync(client, apiUrl, phone, from, reply, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to relay Signal message to core.");
                    await SendSignalMessageAsync(client, apiUrl, phone, from,
                        "\u26a0 Failed to process message.", ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Signal message.");
        }
    }

    private static async Task SendSignalMessageAsync(
        HttpClient client, string apiUrl, string phone,
        string recipient, string text, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                message = text,
                number = Uri.UnescapeDataString(phone),
                recipients = new[] { recipient }
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            await client.PostAsync($"{apiUrl}/v2/send", content, ct);
        }
        catch
        {
            // Best-effort delivery — already logged by caller
        }
    }

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }

    private sealed record SignalChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record SignalChatResponse
    {
        public SignalChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record SignalChatMessageDto
    {
        public string? Content { get; init; }
    }
}
