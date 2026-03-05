using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Context;

/// <summary>
/// A conversation thread within a channel.  Threads enable persistent
/// chat history — messages sent outside a thread are treated as
/// isolated one-shots with no prior context sent to the model.
/// </summary>
public class ChatThreadDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Maximum number of recent messages to send as history.
    /// <c>null</c> means use the system default (50).
    /// </summary>
    public int? MaxMessages { get; set; }

    /// <summary>
    /// Maximum total character count for the history payload.
    /// <c>null</c> means use the system default (100 000).
    /// When both limits are set, messages are trimmed to satisfy both.
    /// </summary>
    public int? MaxCharacters { get; set; }

    public Guid ChannelId { get; set; }
    public ChannelDB Channel { get; set; } = null!;

    public ICollection<ChatMessageDB> ChatMessages { get; set; } = [];
}
