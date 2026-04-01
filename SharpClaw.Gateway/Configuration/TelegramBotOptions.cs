namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Configuration for the Telegram bot integration.
/// Loaded from the <c>Gateway:Bots:Telegram</c> configuration section.
/// </summary>
public sealed class TelegramBotOptions
{
    public const string SectionName = "Gateway:Bots:Telegram";

    /// <summary>Whether the Telegram bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bot API token obtained from <c>@BotFather</c>.</summary>
    public string? BotToken { get; set; }
}
