namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the WhatsApp bot integration.
/// Loaded from the <c>Gateway:Bots:WhatsApp</c> configuration section.
/// <para>
/// WhatsApp-specific settings (phone number ID, verify token) live in
/// the gateway <c>.env</c> because they are platform-configuration
/// concerns rather than per-integration data. The access token is stored
/// in the core database as <c>BotToken</c> alongside other integrations.
/// </para>
/// </summary>
public sealed class WhatsAppBotOptions
{
    public const string SectionName = "Gateway:Bots:WhatsApp";

    /// <summary>Whether the WhatsApp bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// WhatsApp Business phone number ID from the Meta Cloud API dashboard.
    /// Required for sending reply messages.
    /// </summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>
    /// Arbitrary token used to verify the webhook subscription.
    /// Must match the value configured in the Meta webhook settings.
    /// </summary>
    public string? VerifyToken { get; set; }
}
