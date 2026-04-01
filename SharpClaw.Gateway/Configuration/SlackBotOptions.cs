namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Slack bot integration.
/// Loaded from the <c>Gateway:Bots:Slack</c> configuration section.
/// <para>
/// The signing secret lives in the gateway <c>.env</c> because it is a
/// platform-configuration concern (webhook verification). The bot/user
/// OAuth token is stored in the core database as <c>BotToken</c>.
/// </para>
/// </summary>
public sealed class SlackBotOptions
{
    public const string SectionName = "Gateway:Bots:Slack";

    /// <summary>Whether the Slack bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Slack app signing secret used to verify incoming webhook requests.
    /// Found in the Slack app settings under "Basic Information".
    /// </summary>
    public string? SigningSecret { get; set; }
}
