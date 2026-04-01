using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Bots;

/// <summary>
/// Hosted service that runs the Email bot by polling IMAP for unread
/// messages and forwarding them to the SharpClaw core via
/// <see cref="InternalApiClient"/>. Replies are sent via SMTP.
/// <para>
/// Email is inherently 1:1 — every incoming message is treated as a DM.
/// Uses a minimal IMAP client (no external libraries) over TLS.
/// </para>
/// <para>
/// Automatically reloads configuration when <see cref="BotReloadSignal"/>
/// fires.
/// </para>
/// </summary>
public sealed partial class EmailBotService : BackgroundService
{
    private readonly InternalApiClient _coreApi;
    private readonly ILogger<EmailBotService> _logger;
    private readonly BotReloadSignal _reloadSignal;
    private readonly IOptionsMonitor<EmailBotOptions> _options;

    private Guid? _defaultChannelId;
    private Guid? _defaultThreadId;

    public EmailBotService(
        InternalApiClient coreApi,
        ILogger<EmailBotService> logger,
        BotReloadSignal reloadSignal,
        IOptionsMonitor<EmailBotOptions> options)
    {
        _coreApi = coreApi;
        _logger = logger;
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
                    "/bots/config/email", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch Email bot config from core API. Retrying on reload...");
                config = null;
            }

            var opts = _options.CurrentValue;

            if (config is null || !config.Enabled
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                if (config is { Enabled: true } && string.IsNullOrWhiteSpace(config.BotToken))
                    _logger.LogWarning(
                        "Email bot is enabled but no password (BotToken) is configured.");
                else
                    _logger.LogInformation(
                        "Email bot is disabled or not configured. Waiting for reload signal...");

                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (string.IsNullOrWhiteSpace(opts.ImapHost) || string.IsNullOrWhiteSpace(opts.SmtpHost)
                || string.IsNullOrWhiteSpace(opts.Username))
            {
                _logger.LogWarning(
                    "Email bot is enabled but IMAP/SMTP host or Username is not configured in gateway .env.");
                try { await _reloadSignal.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _defaultChannelId = config.DefaultChannelId;
            _defaultThreadId = config.DefaultThreadId;

            _logger.LogInformation("Email bot starting IMAP poll loop...");

            // Inner loop: IMAP poll
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var reloadTask = WaitForReloadAsync(pollCts);

            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    await PollAndProcessAsync(opts, config.BotToken, pollCts.Token);
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(5, opts.PollIntervalSeconds)),
                        pollCts.Token);
                }
                catch (OperationCanceledException) when (pollCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Email IMAP poll error. Retrying in 10 seconds...");
                    try { await Task.Delay(TimeSpan.FromSeconds(10), pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Email bot reloading configuration...");

            await reloadTask;
        }

        _logger.LogInformation("Email bot stopped.");
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

    private async Task PollAndProcessAsync(
        EmailBotOptions opts, string password, CancellationToken ct)
    {
        using var imap = new MiniImapClient();
        await imap.ConnectAsync(opts.ImapHost!, opts.ImapPort, ct);
        await imap.LoginAsync(opts.Username!, password, ct);
        await imap.SelectAsync("INBOX", ct);

        var unseen = await imap.SearchUnseenAsync(ct);
        if (unseen.Length == 0) return;

        _logger.LogInformation("Email bot found {Count} unread messages.", unseen.Length);

        foreach (var uid in unseen)
        {
            try
            {
                var (from, subject, body) = await imap.FetchMessageAsync(uid, ct);
                if (string.IsNullOrWhiteSpace(body))
                    body = subject; // Fall back to subject if body is empty

                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(body))
                    continue;

                var displayName = from;
                var emailAddress = ExtractEmailAddress(from);

                _logger.LogInformation(
                    "Email from {From}: {Subject}", from, subject);

                // Mark as seen
                await imap.StoreSeenAsync(uid, ct);

                if (_defaultChannelId is null)
                {
                    await SendEmailReplyAsync(opts, password, emailAddress, subject,
                        "\u26a0 No default channel configured for this bot.");
                    continue;
                }

                try
                {
                    var chatRequest = new EmailChatRequest(
                        body, null, "Email", emailAddress, displayName);

                    var chatPath = _defaultThreadId is not null
                        ? $"/channels/{_defaultChannelId}/chat/threads/{_defaultThreadId}"
                        : $"/channels/{_defaultChannelId}/chat";

                    var response = await _coreApi
                        .PostAsync<EmailChatRequest, EmailChatResponse>(
                            chatPath, chatRequest, ct);

                    var reply = response?.AssistantMessage?.Content
                        ?? "No response from agent.";
                    await SendEmailReplyAsync(opts, password, emailAddress,
                        $"Re: {subject}", reply);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to relay email to core.");
                    await SendEmailReplyAsync(opts, password, emailAddress,
                        $"Re: {subject}", "\u26a0 Failed to process message.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process email UID {Uid}.", uid);
            }
        }
    }

    private static string ExtractEmailAddress(string from)
    {
        var match = EmailAddressRegex().Match(from);
        return match.Success ? match.Groups[1].Value : from;
    }

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex EmailAddressRegex();

    #pragma warning disable CS0618 // SmtpClient is obsolete but sufficient for simple sends
    private async Task SendEmailReplyAsync(
        EmailBotOptions opts, string password,
        string to, string subject, string body)
    {
        try
        {
            using var smtp = new SmtpClient(opts.SmtpHost!, opts.SmtpPort)
            {
                Credentials = new NetworkCredential(opts.Username, password),
                EnableSsl = true
            };

            var message = new MailMessage(opts.Username!, to, subject, body);
            await smtp.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email reply to {To}.", to);
        }
    }
    #pragma warning restore CS0618

    // ── Minimal IMAP client (TLS, no external deps) ─────────────

    private sealed partial class MiniImapClient : IDisposable
    {
        private TcpClient? _tcp;
        private SslStream? _ssl;
        private StreamReader? _reader;
        private int _tag;

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, port, ct);
            _ssl = new SslStream(_tcp.GetStream(), false);
            await _ssl.AuthenticateAsClientAsync(host);
            _reader = new StreamReader(_ssl, Encoding.UTF8);

            // Read server greeting
            await _reader.ReadLineAsync(ct);
        }

        public async Task LoginAsync(string user, string pass, CancellationToken ct)
            => await SendCommandAsync($"LOGIN \"{user}\" \"{pass}\"", ct);

        public async Task SelectAsync(string mailbox, CancellationToken ct)
            => await SendCommandAsync($"SELECT \"{mailbox}\"", ct);

        public async Task<int[]> SearchUnseenAsync(CancellationToken ct)
        {
            var response = await SendCommandAsync("SEARCH UNSEEN", ct);
            var results = new List<int>();

            foreach (var line in response)
            {
                if (!line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line["* SEARCH".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (int.TryParse(p, out var uid))
                        results.Add(uid);
                }
            }

            return [.. results];
        }

        public async Task<(string From, string Subject, string Body)> FetchMessageAsync(
            int uid, CancellationToken ct)
        {
            var response = await SendCommandAsync($"FETCH {uid} (BODY[HEADER.FIELDS (FROM SUBJECT)] BODY[TEXT])", ct);
            var full = string.Join("\n", response);

            var from = "";
            var subject = "";
            var body = "";

            var fromMatch = ImapFromRegex().Match(full);
            if (fromMatch.Success) from = fromMatch.Groups[1].Value.Trim();

            var subjectMatch = ImapSubjectRegex().Match(full);
            if (subjectMatch.Success) subject = subjectMatch.Groups[1].Value.Trim();

            // Body is everything after the header section
            var bodyStart = full.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart < 0) bodyStart = full.IndexOf("\n\n", StringComparison.Ordinal);
            if (bodyStart >= 0) body = full[(bodyStart + 2)..].Trim();

            // Clean up IMAP artifacts from body
            if (body.Contains(')'))
                body = body[..body.LastIndexOf(')')].Trim();

            return (from, subject, body);
        }

        public async Task StoreSeenAsync(int uid, CancellationToken ct)
            => await SendCommandAsync($"STORE {uid} +FLAGS (\\Seen)", ct);

        private async Task<List<string>> SendCommandAsync(string command, CancellationToken ct)
        {
            var tag = $"A{++_tag:D4}";
            var line = $"{tag} {command}\r\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await _ssl!.WriteAsync(bytes, ct);
            await _ssl.FlushAsync(ct);

            var lines = new List<string>();
            while (true)
            {
                var resp = await _reader!.ReadLineAsync(ct);
                if (resp is null) break;

                lines.Add(resp);
                if (resp.StartsWith($"{tag} ", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            return lines;
        }

        [GeneratedRegex(@"From:\s*(.+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImapFromRegex();

        [GeneratedRegex(@"Subject:\s*(.+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImapSubjectRegex();

        public void Dispose()
        {
            _reader?.Dispose();
            _ssl?.Dispose();
            _tcp?.Dispose();
        }
    }

    // ── Core API DTOs ────────────────────────────────────────────

    private sealed record BotConfigResponse
    {
        public bool Enabled { get; init; }
        public string? BotToken { get; init; }
        public Guid? DefaultChannelId { get; init; }
        public Guid? DefaultThreadId { get; init; }
    }

    private sealed record EmailChatRequest(
        string Message,
        Guid? AgentId,
        string ClientType,
        string? ExternalUsername,
        string? ExternalDisplayName);

    private sealed record EmailChatResponse
    {
        public EmailChatMessageDto? AssistantMessage { get; init; }
    }

    private sealed record EmailChatMessageDto
    {
        public string? Content { get; init; }
    }
}
