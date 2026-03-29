using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Registers minimal API endpoints for the WhatsApp Cloud API webhook.
/// <list type="bullet">
/// <item><c>GET /api/bots/whatsapp/webhook</c> — Meta verification challenge</item>
/// <item><c>POST /api/bots/whatsapp/webhook</c> — Incoming message handler</item>
/// </list>
/// </summary>
public static class WhatsAppWebhookProxy
{
    private const string GraphApiBase = "https://graph.facebook.com/v21.0/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapWhatsAppWebhookProxy(this WebApplication app)
    {
        // Meta sends a GET to verify the webhook subscription
        app.MapGet("/api/bots/whatsapp/webhook", (
            [FromQuery(Name = "hub.mode")] string? mode,
            [FromQuery(Name = "hub.verify_token")] string? verifyToken,
            [FromQuery(Name = "hub.challenge")] string? challenge,
            WhatsAppBotState state) =>
        {
            if (mode != "subscribe")
                return Results.BadRequest(new { error = "Invalid hub.mode." });

            var config = state.Current;
            if (config is null || !config.Enabled)
                return Results.StatusCode(503);

            if (string.IsNullOrWhiteSpace(config.VerifyToken)
                || verifyToken != config.VerifyToken)
                return Results.Unauthorized();

            // Return the challenge as plain text (Meta requirement)
            return Results.Text(challenge ?? "");
        });

        // Meta sends a POST with incoming message events
        app.MapPost("/api/bots/whatsapp/webhook", async (
            HttpContext httpContext,
            WhatsAppBotState state,
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

            var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Bots.WhatsAppWebhook");

            // Fire-and-forget — Meta expects 200 within 5s; agent inference takes much longer
            _ = Task.Run(() => ProcessWebhookPayloadAsync(
                body, config, coreApi, httpClientFactory, logger, CancellationToken.None));

            return Results.Ok();
        });
    }

    private static async Task ProcessWebhookPayloadAsync(
        string body,
        WhatsAppBotState.WhatsAppConfig config,
        InternalApiClient coreApi,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<WebhookPayload>(body, JsonOptions);
            if (payload?.Entry is null) return;

            foreach (var entry in payload.Entry)
            {
                if (entry.Changes is null) continue;

                foreach (var change in entry.Changes)
                {
                    if (change.Value?.Messages is null) continue;

                    // Build a lookup of contacts for display name resolution
                    var contacts = change.Value.Contacts?
                        .ToDictionary(c => c.WaId ?? "", c => c.Profile?.Name ?? "")
                        ?? [];

                    foreach (var message in change.Value.Messages)
                    {
                        if (message.Type != "text" || message.Text?.Body is null)
                            continue;

                        var from = message.From ?? "unknown";
                        var displayName = contacts.GetValueOrDefault(from, from);
                        var text = message.Text.Body;

                        logger.LogInformation(
                            "WhatsApp message from {From} ({Name}): {Text}",
                            from, displayName, text);

                        if (config.DefaultChannelId is null)
                        {
                            await SendWhatsAppMessageAsync(httpClientFactory, config,
                                from, "\u26a0 No default channel configured for this bot.",
                                logger, ct);
                            continue;
                        }

                        try
                        {
                            var chatRequest = new WhatsAppChatRequest(
                                text, null, "WhatsApp", from, displayName);

                            var chatPath = config.DefaultThreadId is not null
                                ? $"/channels/{config.DefaultChannelId}/chat/threads/{config.DefaultThreadId}"
                                : $"/channels/{config.DefaultChannelId}/chat";

                            var response = await coreApi
                                .PostAsync<WhatsAppChatRequest, WhatsAppChatResponse>(
                                    chatPath, chatRequest, ct);

                            var reply = response?.AssistantMessage?.Content
                                ?? "No response from agent.";
                            await SendWhatsAppMessageAsync(httpClientFactory, config,
                                from, reply, logger, ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to relay WhatsApp message to core.");
                            await SendWhatsAppMessageAsync(httpClientFactory, config,
                                from, "\u26a0 Failed to process message.", logger, ct);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process WhatsApp webhook payload.");
        }
    }

    private static async Task SendWhatsAppMessageAsync(
        IHttpClientFactory httpClientFactory,
        WhatsAppBotState.WhatsAppConfig config,
        string to,
        string text,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("WhatsAppBot");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AccessToken);

            var payload = JsonSerializer.Serialize(new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = text }
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            await client.PostAsync(
                $"{GraphApiBase}{config.PhoneNumberId}/messages", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send WhatsApp message to {To}.", to);
        }
    }

    // ── WhatsApp Cloud API webhook models (simplified) ───────────

    private sealed record WebhookPayload
    {
        public WebhookEntry[]? Entry { get; init; }
    }

    private sealed record WebhookEntry
    {
        public WebhookChange[]? Changes { get; init; }
    }

    private sealed record WebhookChange
    {
        public WebhookValue? Value { get; init; }
    }

    private sealed record WebhookValue
    {
        public WebhookMessage[]? Messages { get; init; }
        public WebhookContact[]? Contacts { get; init; }
    }

    private sealed record WebhookMessage
    {
        public string? From { get; init; }
        public string? Type { get; init; }
        public WebhookTextBody? Text { get; init; }
    }

    private sealed record WebhookTextBody
    {
        public string? Body { get; init; }
    }

    private sealed record WebhookContact
    {
        public WebhookProfile? Profile { get; init; }
        public string? WaId { get; init; }
    }

    private sealed record WebhookProfile
    {
        public string? Name { get; init; }
    }

    // ── Core API chat DTOs ───────────────────────────────────────

    private sealed record WhatsAppChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record WhatsAppChatResponse
    {
        public WhatsAppChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record WhatsAppChatMessageDto
    {
        public string? Content { get; init; }
    }
}
