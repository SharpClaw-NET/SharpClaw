using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// Sends outbound messages through bot integrations on behalf of agents.
/// Each platform's HTTP API is called directly from the core, using the
/// decrypted bot token and platform-specific config stored on
/// <see cref="BotIntegrationDB"/>.
/// </summary>
public sealed class BotMessageSenderService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Sends a message through the specified bot integration.
    /// </summary>
    /// <param name="botIntegrationId">The bot integration resource ID.</param>
    /// <param name="recipientId">Platform-specific recipient (chat ID, phone, email, room ID, etc.).</param>
    /// <param name="message">Message body text.</param>
    /// <param name="subject">Optional subject line (email only).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A human-readable result string for the agent.</returns>
    public async Task<string> SendMessageAsync(
        Guid botIntegrationId, string recipientId, string message,
        string? subject = null, CancellationToken ct = default)
    {
        var bot = await db.BotIntegrations.FirstOrDefaultAsync(b => b.Id == botIntegrationId, ct)
            ?? throw new InvalidOperationException($"Bot integration {botIntegrationId} not found.");

        if (!bot.Enabled)
            throw new InvalidOperationException($"Bot integration '{bot.Name}' is disabled.");

        if (bot.EncryptedBotToken is null)
            throw new InvalidOperationException($"Bot integration '{bot.Name}' has no token configured.");

        var token = ApiKeyEncryptor.Decrypt(bot.EncryptedBotToken, encryptionOptions.Key);

        var platformConfig = bot.PlatformConfig is not null
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(bot.PlatformConfig, _jsonOptions)
            : null;

        return bot.BotType switch
        {
            BotType.Telegram => await SendTelegramAsync(token, recipientId, message, ct),
            BotType.Discord => await SendDiscordAsync(token, recipientId, message, ct),
            BotType.WhatsApp => await SendWhatsAppAsync(token, recipientId, message, platformConfig, ct),
            BotType.Slack => await SendSlackAsync(token, recipientId, message, ct),
            BotType.Matrix => await SendMatrixAsync(token, recipientId, message, platformConfig, ct),
            BotType.Signal => await SendSignalAsync(recipientId, message, platformConfig, ct),
            BotType.Email => await SendEmailAsync(token, recipientId, message, subject, platformConfig, ct),
            BotType.Teams => await SendTeamsAsync(token, recipientId, message, platformConfig, ct),
            _ => throw new InvalidOperationException($"Unsupported bot type: {bot.BotType}.")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Platform senders
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> SendTelegramAsync(
        string token, string chatId, string message, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var payload = new { chat_id = chatId, text = message };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Telegram message sent to chat {chatId}.";
    }

    private async Task<string> SendDiscordAsync(
        string token, string channelId, string message, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bot {token}");
        var url = $"https://discord.com/api/v10/channels/{channelId}/messages";

        // Discord has a 2000-char limit
        var text = message.Length > 2000 ? message[..2000] : message;
        var payload = new { content = text };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Discord message sent to channel {channelId}.";
    }

    private async Task<string> SendWhatsAppAsync(
        string token, string phone, string message,
        Dictionary<string, string>? config, CancellationToken ct)
    {
        var phoneNumberId = config?.GetValueOrDefault("PhoneNumberId")
            ?? throw new InvalidOperationException("WhatsApp PlatformConfig missing 'PhoneNumberId'.");

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var url = $"https://graph.facebook.com/v21.0/{phoneNumberId}/messages";
        var payload = new
        {
            messaging_product = "whatsapp",
            to = phone,
            type = "text",
            text = new { body = message }
        };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"WhatsApp message sent to {phone}.";
    }

    private async Task<string> SendSlackAsync(
        string token, string channel, string message, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var url = "https://slack.com/api/chat.postMessage";
        var payload = new { channel, text = message };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Slack message sent to {channel}.";
    }

    private async Task<string> SendMatrixAsync(
        string token, string roomId, string message,
        Dictionary<string, string>? config, CancellationToken ct)
    {
        var homeserver = config?.GetValueOrDefault("HomeserverUrl")
            ?? throw new InvalidOperationException("Matrix PlatformConfig missing 'HomeserverUrl'.");

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var txnId = Guid.NewGuid().ToString("N");
        var url = $"{homeserver.TrimEnd('/')}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{txnId}";
        var payload = new { msgtype = "m.text", body = message };
        var response = await client.PutAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Matrix message sent to room {roomId}.";
    }

    private async Task<string> SendSignalAsync(
        string phone, string message,
        Dictionary<string, string>? config, CancellationToken ct)
    {
        var apiUrl = config?.GetValueOrDefault("ApiUrl")
            ?? throw new InvalidOperationException("Signal PlatformConfig missing 'ApiUrl'.");
        var senderPhone = config?.GetValueOrDefault("PhoneNumber")
            ?? throw new InvalidOperationException("Signal PlatformConfig missing 'PhoneNumber'.");

        using var client = httpClientFactory.CreateClient();
        var url = $"{apiUrl.TrimEnd('/')}/v2/send";
        var payload = new
        {
            message,
            number = senderPhone,
            recipients = new[] { phone }
        };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Signal message sent to {phone}.";
    }

    private async Task<string> SendEmailAsync(
        string password, string recipient, string message,
        string? subject, Dictionary<string, string>? config, CancellationToken ct)
    {
        var smtpHost = config?.GetValueOrDefault("SmtpHost")
            ?? throw new InvalidOperationException("Email PlatformConfig missing 'SmtpHost'.");
        var smtpPortStr = config?.GetValueOrDefault("SmtpPort") ?? "587";
        var username = config?.GetValueOrDefault("Username")
            ?? throw new InvalidOperationException("Email PlatformConfig missing 'Username'.");

        if (!int.TryParse(smtpPortStr, out var smtpPort))
            smtpPort = 587;

        var emailSubject = subject ?? "Message from SharpClaw";

        // Minimal SMTP send via TcpClient + SslStream (same approach as EmailBotService)
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(smtpHost, smtpPort, ct);

        await using var rawStream = tcp.GetStream();
        await using var ssl = new System.Net.Security.SslStream(rawStream, false);
        await ssl.AuthenticateAsClientAsync(smtpHost);

        using var reader = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(ssl, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        await reader.ReadLineAsync(ct); // greeting

        await SmtpCommandAsync(writer, reader, $"EHLO localhost", ct);
        await SmtpCommandAsync(writer, reader, $"AUTH LOGIN", ct);
        await SmtpCommandAsync(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), ct);
        await SmtpCommandAsync(writer, reader, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), ct);
        await SmtpCommandAsync(writer, reader, $"MAIL FROM:<{username}>", ct);
        await SmtpCommandAsync(writer, reader, $"RCPT TO:<{recipient}>", ct);
        await SmtpCommandAsync(writer, reader, "DATA", ct);

        await writer.WriteLineAsync($"From: {username}");
        await writer.WriteLineAsync($"To: {recipient}");
        await writer.WriteLineAsync($"Subject: {emailSubject}");
        await writer.WriteLineAsync("Content-Type: text/plain; charset=utf-8");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync(message);
        await SmtpCommandAsync(writer, reader, ".", ct);
        await SmtpCommandAsync(writer, reader, "QUIT", ct);

        return $"Email sent to {recipient}.";
    }

    private static async Task SmtpCommandAsync(
        StreamWriter writer, StreamReader reader, string command, CancellationToken ct)
    {
        await writer.WriteLineAsync(command.AsMemory(), ct);
        await reader.ReadLineAsync(ct);
    }

    private async Task<string> SendTeamsAsync(
        string clientSecret, string conversationId, string message,
        Dictionary<string, string>? config, CancellationToken ct)
    {
        var appId = config?.GetValueOrDefault("AppId")
            ?? throw new InvalidOperationException("Teams PlatformConfig missing 'AppId'.");
        var serviceUrl = config?.GetValueOrDefault("ServiceUrl")
            ?? "https://smba.trafficmanager.net/teams/";

        // Acquire Bot Framework OAuth token
        using var client = httpClientFactory.CreateClient();
        var tokenUrl = "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = appId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://api.botframework.com/.default"
        });
        var tokenResp = await client.PostAsync(tokenUrl, tokenForm, ct);
        tokenResp.EnsureSuccessStatusCode();
        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var accessToken = tokenJson.GetProperty("access_token").GetString()!;

        // Send proactive message
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{conversationId}/activities";
        var payload = new
        {
            type = "message",
            text = message
        };
        var response = await client.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return $"Teams message sent to conversation {conversationId}.";
    }
}
