namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Email bot integration.
/// Loaded from the <c>Gateway:Bots:Email</c> configuration section.
/// <para>
/// Uses IMAP to poll for incoming messages and SMTP to send replies.
/// The password is stored in the core database as <c>BotToken</c>.
/// Host/port/username settings live in the gateway <c>.env</c>.
/// </para>
/// </summary>
public sealed class EmailBotOptions
{
    public const string SectionName = "Gateway:Bots:Email";

    /// <summary>Whether the Email bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>IMAP server hostname (e.g. <c>imap.gmail.com</c>).</summary>
    public string? ImapHost { get; set; }

    /// <summary>IMAP server port (default 993 for TLS).</summary>
    public int ImapPort { get; set; } = 993;

    /// <summary>SMTP server hostname (e.g. <c>smtp.gmail.com</c>).</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP server port (default 587 for STARTTLS).</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Email account username / address.</summary>
    public string? Username { get; set; }

    /// <summary>How often (in seconds) to poll IMAP for new messages.</summary>
    public int PollIntervalSeconds { get; set; } = 30;
}
