namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Discord bot integration.
/// Loaded from the <c>Gateway:Bots:Discord</c> configuration section.
/// </summary>
public sealed class DiscordBotOptions
{
    public const string SectionName = "Gateway:Bots:Discord";

    /// <summary>Whether the Discord bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bot token from the Discord Developer Portal.</summary>
    public string? BotToken { get; set; }
}
