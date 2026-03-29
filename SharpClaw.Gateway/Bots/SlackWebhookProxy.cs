using System.Text;
using System.Text.Json;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Registers minimal API endpoints for the Slack Events API webhook.
/// <list type="bullet">
/// <item><c>POST /api/bots/slack/events</c> — Slack event handler (url_verification + event_callback)</item>
/// </list>
/// Only direct messages (DMs) are processed; channel/group messages are ignored.
/// </summary>
public static class SlackWebhookProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapSlackWebhookProxy(this WebApplication app)
    {
        app.MapPost("/api/bots/slack/events", async (
            HttpContext httpContext,
            SlackBotState state,
            InternalApiClient coreApi,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory) =>
        {
            var config = state.Current;
            if (config is null || !state.IsConfigured)
                return Results.StatusCode(503);

            string body;
            using (var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Bots.SlackWebhook");

            // Handle url_verification challenge (Slack sends this during app setup)
            var doc = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
            if (doc.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "url_verification"
                && doc.TryGetProperty("challenge", out var challengeProp))
            {
                return Results.Json(new { challenge = challengeProp.GetString() });
            }

            // Fire-and-forget — Slack expects 200 within 3 seconds
            _ = Task.Run(() => ProcessEventPayloadAsync(
                body, config, coreApi, httpClientFactory, logger, CancellationToken.None));

            return Results.Ok();
        });
    }

    private static async Task ProcessEventPayloadAsync(
        string body,
        SlackBotState.SlackConfig config,
        InternalApiClient coreApi,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<EventPayload>(body, JsonOptions);
            if (payload?.Type != "event_callback" || payload.Event is null)
                return;

            var evt = payload.Event;

            // Only handle text messages (not subtypes like bot_message, file_share, etc.)
            if (evt.Type != "message" || evt.Subtype is not null)
                return;

            // Only handle direct messages (DMs) — skip channels/groups
            if (evt.ChannelType != "im")
                return;

            // Skip messages from bots
            if (evt.BotId is not null)
                return;

            var from = evt.User ?? "unknown";
            var text = evt.Text;
            if (string.IsNullOrWhiteSpace(text))
                return;

            logger.LogInformation("Slack DM from {User}: {Text}", from, text);

            if (config.DefaultChannelId is null)
            {
                await SendSlackMessageAsync(httpClientFactory, config,
                    evt.Channel!, "\u26a0 No default channel configured for this bot.",
                    logger, ct);
                return;
            }

            try
            {
                var chatRequest = new SlackChatRequest(
                    text, null, "Slack", from, from);

                var chatPath = config.DefaultThreadId is not null
                    ? $"/channels/{config.DefaultChannelId}/chat/threads/{config.DefaultThreadId}"
                    : $"/channels/{config.DefaultChannelId}/chat";

                var response = await coreApi
                    .PostAsync<SlackChatRequest, SlackChatResponse>(
                        chatPath, chatRequest, ct);

                var reply = response?.AssistantMessage?.Content
                    ?? "No response from agent.";
                await SendSlackMessageAsync(httpClientFactory, config,
                    evt.Channel!, reply, logger, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to relay Slack message to core.");
                await SendSlackMessageAsync(httpClientFactory, config,
                    evt.Channel!, "\u26a0 Failed to process message.", logger, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Slack event payload.");
        }
    }

    private static async Task SendSlackMessageAsync(
        IHttpClientFactory httpClientFactory,
        SlackBotState.SlackConfig config,
        string channel,
        string text,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("SlackBot");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BotToken);

            var payload = JsonSerializer.Serialize(new { channel, text });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            await client.PostAsync("https://slack.com/api/chat.postMessage", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Slack message to {Channel}.", channel);
        }
    }

    // ── Slack Events API models (simplified) ─────────────────────

    private sealed record EventPayload
    {
        public string? Type { get; init; }
        public SlackEvent? Event { get; init; }
    }

    private sealed record SlackEvent
    {
        public string? Type { get; init; }
        public string? Subtype { get; init; }
        public string? ChannelType { get; init; }
        public string? Channel { get; init; }
        public string? User { get; init; }
        public string? Text { get; init; }
        public string? BotId { get; init; }
    }

    // ── Core API chat DTOs ───────────────────────────────────────

    private sealed record SlackChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record SlackChatResponse
    {
        public SlackChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record SlackChatMessageDto
    {
        public string? Content { get; init; }
    }
}
