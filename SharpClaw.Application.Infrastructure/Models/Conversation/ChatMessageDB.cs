using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Conversation;

public class ChatMessageDB : BaseEntity
{
    public required string Role { get; set; }
    public required string Content { get; set; }

    public Guid ConversationId { get; set; }
    public ConversationDB Conversation { get; set; } = null!;
}
