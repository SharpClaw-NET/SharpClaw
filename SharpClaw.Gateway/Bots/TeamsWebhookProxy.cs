using System.Text;
using System.Text.Json;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Registers minimal API endpoints for the Microsoft Teams Bot Framework
/// webhook (messaging endpoint).
/// <list type="bullet">
/// <item><c>POST /api/bots/teams/messages</c> — Bot Framework activity handler</item>
/// </list>
/// Only personal (1:1) conversations are processed; group/channel/team
/// messages are ignored.
/// </summary>
public static class TeamsWebhookProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapTeamsWebhookProxy(this WebApplication app)
    {
        app.MapPost("/api/bots/teams/messages", async (
            HttpContext httpContext,
            TeamsBotState state,
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

            var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Bots.TeamsWebhook");

            // Fire-and-forget — respond quickly to Bot Framework
            _ = Task.Run(() => ProcessActivityAsync(
                body, config, coreApi, httpClientFactory, logger, CancellationToken.None));

            return Results.Ok();
        });
    }

    private static async Task ProcessActivityAsync(
        string body,
        TeamsBotState.TeamsConfig config,
        InternalApiClient coreApi,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var activity = JsonSerializer.Deserialize<BotActivity>(body, JsonOptions);
            if (activity is null || activity.Type != "message")
                return;

            // Only handle personal (1:1 DM) conversations
            if (activity.Conversation?.ConversationType != "personal")
                return;

            var text = activity.Text;
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Strip bot @mention from the text (Teams prefixes it in some clients)
            if (activity.Recipient?.Name is { } botName && text.Contains(botName))
                text = text.Replace($"<at>{botName}</at>", "").Trim();

            var from = activity.From?.Name ?? activity.From?.Id ?? "unknown";
            var fromId = activity.From?.Id ?? "unknown";

            logger.LogInformation("Teams DM from {From} ({Id}): {Text}", from, fromId, text);

            if (config.DefaultChannelId is null)
            {
                await SendTeamsReplyAsync(httpClientFactory, config, activity,
                    "\u26a0 No default channel configured for this bot.", logger, ct);
                return;
            }

            try
            {
                var chatRequest = new TeamsChatRequest(
                    text, null, "Teams", fromId, from);

                var chatPath = config.DefaultThreadId is not null
                    ? $"/channels/{config.DefaultChannelId}/chat/threads/{config.DefaultThreadId}"
                    : $"/channels/{config.DefaultChannelId}/chat";

                var response = await coreApi
                    .PostAsync<TeamsChatRequest, TeamsChatResponse>(
                        chatPath, chatRequest, ct);

                var reply = response?.AssistantMessage?.Content
                    ?? "No response from agent.";
                await SendTeamsReplyAsync(httpClientFactory, config, activity,
                    reply, logger, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to relay Teams message to core.");
                await SendTeamsReplyAsync(httpClientFactory, config, activity,
                    "\u26a0 Failed to process message.", logger, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Teams activity.");
        }
    }

    private static async Task SendTeamsReplyAsync(
        IHttpClientFactory httpClientFactory,
        TeamsBotState.TeamsConfig config,
        BotActivity activity,
        string text,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var serviceUrl = activity.ServiceUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(serviceUrl) || activity.Conversation?.Id is null)
                return;

            // Acquire an OAuth2 token for Bot Framework
            var token = await AcquireBotTokenAsync(httpClientFactory, config, logger, ct);
            if (token is null) return;

            var client = httpClientFactory.CreateClient("TeamsBot");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var replyActivity = new
            {
                type = "message",
                text,
                replyToId = activity.Id
            };

            var payload = JsonSerializer.Serialize(replyActivity);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var url = $"{serviceUrl}/v3/conversations/{activity.Conversation.Id}/activities/{activity.Id}";
            await client.PostAsync(url, content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Teams reply.");
        }
    }

    private static async Task<string?> AcquireBotTokenAsync(
        IHttpClientFactory httpClientFactory,
        TeamsBotState.TeamsConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("TeamsBot");

            var form = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", config.AppId!),
                new KeyValuePair<string, string>("client_secret", config.BotToken!),
                new KeyValuePair<string, string>("scope", "https://api.botframework.com/.default"),
            ]);

            var response = await client.PostAsync(
                "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token",
                form, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Teams OAuth2 token acquisition failed: {Status}.", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenDoc = JsonSerializer.Deserialize<JsonElement>(json);
            return tokenDoc.GetProperty("access_token").GetString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acquire Teams OAuth2 token.");
            return null;
        }
    }

    // ── Bot Framework activity models (simplified) ───────────────

    private sealed record BotActivity
    {
        public string? Id { get; init; }
        public string? Type { get; init; }
        public string? Text { get; init; }
        public string? ServiceUrl { get; init; }
        public BotChannelAccount? From { get; init; }
        public BotChannelAccount? Recipient { get; init; }
        public BotConversation? Conversation { get; init; }
    }

    private sealed record BotChannelAccount
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
    }

    private sealed record BotConversation
    {
        public string? Id { get; init; }
        public string? ConversationType { get; init; }
    }

    // ── Core API chat DTOs ───────────────────────────────────────

    private sealed record TeamsChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record TeamsChatResponse
    {
        public TeamsChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record TeamsChatMessageDto
    {
        public string? Content { get; init; }
    }
}
