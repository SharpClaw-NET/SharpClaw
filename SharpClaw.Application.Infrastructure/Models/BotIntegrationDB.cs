using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models;

/// <summary>
/// Stores a bot integration configuration (Telegram, Discord, etc.)
/// in the core database instead of the gateway .env file.
/// Bot tokens are encrypted at rest using the same mechanism as
/// <see cref="SharpClaw.Infrastructure.Models.ProviderDB.EncryptedApiKey"/>.
/// </summary>
public class BotIntegrationDB : BaseEntity
{
    /// <summary>User-visible display name for this integration.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Type of bot platform.</summary>
    public required BotType BotType { get; set; }

    /// <summary>Whether the bot is enabled and should be started by the gateway.</summary>
    public bool Enabled { get; set; }

    /// <summary>AES-GCM encrypted bot token. Decrypted at runtime.</summary>
    [HeaderSensitive]
    public string? EncryptedBotToken { get; set; }

    /// <summary>
    /// Optional channel ID that incoming messages are routed to.
    /// When set, the gateway forwards bot messages to this channel.
    /// </summary>
    public Guid? DefaultChannelId { get; set; }

    /// <summary>
    /// Optional thread ID within the default channel.
    /// Messages are posted to this thread for persistent conversation history.
    /// Auto-assigned when <see cref="DefaultChannelId"/> is set.
    /// </summary>
    public Guid? DefaultThreadId { get; set; }
}
