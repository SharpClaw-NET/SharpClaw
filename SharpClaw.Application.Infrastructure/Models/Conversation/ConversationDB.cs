using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Conversation;

/// <summary>
/// A conversation that optionally belongs to an agent context.  Each
/// conversation tracks its own model (which can change mid-conversation),
/// title, and chat history.
/// <para>
/// When the conversation belongs to a context, the context's permission
/// set acts as a default — it is used unless overridden by the
/// conversation's own permission set.
/// </para>
/// </summary>
public class ConversationDB : BaseEntity
{
    public required string Title { get; set; }

    /// <summary>
    /// The model currently used by this conversation. Can be changed
    /// mid-conversation independently of the agent's default model.
    /// </summary>
    public Guid ModelId { get; set; }
    public ModelDB Model { get; set; } = null!;

    /// <summary>
    /// The agent that owns this conversation. Always required — even for
    /// standalone conversations the agent identity is needed for the
    /// system prompt and provider resolution.
    /// </summary>
    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;

    /// <summary>
    /// Optional context this conversation belongs to.  When set, the
    /// context's permission set acts as a default for this conversation.
    /// </summary>
    public Guid? AgentContextId { get; set; }
    public AgentContextDB? AgentContext { get; set; }

    /// <summary>
    /// Optional permission set for this conversation. Overrides the
    /// context's permission set when present.
    /// </summary>
    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    public ICollection<ChatMessageDB> ChatMessages { get; set; } = [];
}
