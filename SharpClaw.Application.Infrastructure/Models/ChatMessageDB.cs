using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

public class ChatMessageDB : BaseEntity
{
    public required string Role { get; set; }
    public required string Content { get; set; }

    public Guid ConversationId { get; set; }
    public ConversationDB Conversation { get; set; } = null!;
}
