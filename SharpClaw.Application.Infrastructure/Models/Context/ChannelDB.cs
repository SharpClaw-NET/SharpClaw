using SharpClaw.Application.Infrastructure.Models;
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
    /// Optional custom chat header template for this channel.  When set,
    /// overrides the agent's <see cref="AgentDB.CustomChatHeader"/> and
    /// the default system header.  Tag placeholders are expanded at runtime.
    /// </summary>
    public string? CustomChatHeader { get; set; }

    /// <summary>
    /// When <see langword="true"/>, no tool schemas or tool instruction
    /// suffix are sent in chat requests on this channel — the model sees
    /// only the system prompt and conversation history.  Overrides the
    /// agent-level <see cref="AgentDB.DisableToolSchemas"/> setting.
    /// </summary>
    public bool DisableToolSchemas { get; set; }

    /// <summary>
    /// Optional tool-awareness set controlling which tool-call schemas are
    /// sent in API requests.  Overrides the agent's set when present.
    /// <para>
    /// Override chain: this set → agent's set → <see langword="null"/>
    /// (all tools enabled).
    /// </para>
    /// Ignored when <see cref="DisableToolSchemas"/> is <see langword="true"/>.
    /// </summary>
    public Guid? ToolAwarenessSetId { get; set; }
    public ToolAwarenessSetDB? ToolAwarenessSet { get; set; }

    /// <summary>
    /// Additional agents allowed to operate on this channel.
    /// primary <see cref="Agent"/> is always implicitly allowed and
    /// is NOT included in this collection.  When a job or chat
    /// specifies a non-default agent, it must be in this set.
    /// </summary>
    public ICollection<AgentDB> AllowedAgents { get; set; } = [];

    public ICollection<ChatMessageDB> ChatMessages { get; set; } = [];

    public ICollection<ChatThreadDB> Threads { get; set; } = [];
}
