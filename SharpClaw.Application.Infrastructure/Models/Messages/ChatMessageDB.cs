using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Messages;

public class ChatMessageDB : BaseEntity
{
    public required string Role { get; set; }
    public required string Content { get; set; }

    public Guid ChannelId { get; set; }
    public ChannelDB Channel { get; set; } = null!;

    /// <summary>
    /// Optional thread this message belongs to.  Messages without a
    /// thread are treated as isolated one-shots with no history sent
    /// to the model.
    /// </summary>
    public Guid? ThreadId { get; set; }
    public ChatThreadDB? Thread { get; set; }

    // ── Sender metadata ───────────────────────────────────────────

    /// <summary>
    /// The authenticated user who sent a <c>user</c>-role message, or
    /// <see langword="null"/> for <c>assistant</c> messages.
    /// </summary>
    public Guid? SenderUserId { get; set; }

    /// <summary>
    /// Snapshot of the sender's username at the time the message was
    /// created. Avoids a join to resolve display names in history.
    /// </summary>
    public string? SenderUsername { get; set; }

    /// <summary>
    /// The agent that generated an <c>assistant</c>-role message, or
    /// <see langword="null"/> for <c>user</c> messages.
    /// </summary>
    public Guid? SenderAgentId { get; set; }

    /// <summary>
    /// Snapshot of the agent's name at the time the message was created.
    /// </summary>
    public string? SenderAgentName { get; set; }

    /// <summary>
    /// Which client interface originated this message.
    /// </summary>
    public ChatClientType? ClientType { get; set; }

    // ── Token usage (assistant messages only) ─────────────────────

    /// <summary>
    /// Number of prompt tokens consumed when generating this
    /// assistant response. Null for user messages or when the provider
    /// did not report usage.
    /// </summary>
    public int? PromptTokens { get; set; }

    /// <summary>
    /// Number of completion tokens generated for this assistant
    /// response. Null for user messages or when the provider did not
    /// report usage.
    /// </summary>
    public int? CompletionTokens { get; set; }
}
