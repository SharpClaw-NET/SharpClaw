using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Context;

/// <summary>
/// A channel that optionally belongs to an agent context.  Each
/// channel tracks its own title and chat history.  The model used
/// for completions is always the resolved agent's model.
/// <para>
/// When the channel belongs to a context, the context's permission
/// set acts as a default — it is used unless overridden by the
/// channel's own permission set.
/// </para>
/// </summary>
public class ChannelDB : BaseEntity
{
    public required string Title { get; set; }

    /// <summary>
    /// The default agent for this channel.  May be <see langword="null"/>
    /// when the channel belongs to a context — in that case the context's
    /// agent is used as the fallback.
    /// </summary>
    public Guid? AgentId { get; set; }
    public AgentDB? Agent { get; set; }

    /// <summary>
    /// Optional context this channel belongs to.  When set, the
    /// context's permission set acts as a default for this channel.
    /// </summary>
    public Guid? AgentContextId { get; set; }
    public ChannelContextDB? AgentContext { get; set; }

    /// <summary>
    /// Optional permission set for this channel. Overrides the
    /// context's permission set when present.
    /// </summary>
    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    /// <summary>
    /// Optional default resources for this channel.  When a job is
    /// submitted without a resource ID, this set is checked first.
    /// Falls back to the context's default resource set.
    /// </summary>
    public Guid? DefaultResourceSetId { get; set; }
    public DefaultResourceSetDB? DefaultResourceSet { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the per-message user metadata header
    /// is not prepended to messages sent on this channel. Overrides the
    /// context-level setting.
    /// </summary>
    public bool DisableChatHeader { get; set; }

    /// <summary>
    /// Additional agents allowed to operate on this channel.  The
    /// primary <see cref="Agent"/> is always implicitly allowed and
    /// is NOT included in this collection.  When a job or chat
    /// specifies a non-default agent, it must be in this set.
    /// </summary>
    public ICollection<AgentDB> AllowedAgents { get; set; } = [];

    public ICollection<ChatMessageDB> ChatMessages { get; set; } = [];
}
