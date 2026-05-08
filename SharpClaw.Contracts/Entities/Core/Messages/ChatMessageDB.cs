using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.Entities.Core.Messages;

public class ChatMessageDB : BaseEntity
{
    /// <summary>
    /// Provider-facing role string for this message. This is the value
    /// LLM providers (OpenAI, Anthropic, Google, OpenRouter, …) require
    /// on every message in the conversation history they receive: one
    /// of <c>"user"</c>, <c>"assistant"</c>, <c>"system"</c>, or
    /// <c>"tool"</c> (see <see cref="SharpClaw.Contracts.Chat.ChatRoles"/>).
    /// Providers use it to decide how the message is rendered into the
    /// prompt and which messages are tool-call results being fed back.
    /// <para>
    /// This field is part of the wire protocol — do <b>not</b> use it
    /// for SharpClaw-internal "who sent this" decisions. Use
    /// <see cref="Origin"/> for that. The two intentionally do not
    /// have to agree (e.g. a SharpClaw-injected error notice may have
    /// <c>Role = "system"</c> and <c>Origin = System</c>; a future
    /// transcribed-audio prompt may have <c>Role = "user"</c> with a
    /// more specific <c>Origin</c>).
    /// </para>
    /// </summary>
    public required string Role { get; set; }

    /// <summary>
    /// SharpClaw's own classification of who produced this message,
    /// independent of the provider <see cref="Role"/> string. Used for
    /// internal logic (deduplication, audit, UI grouping) so we do not
    /// have to compare against the stringly-typed wire values.
    /// <para>
    /// Nullable: messages written before this column existed leave it
    /// as <c>null</c>; callers should fall back to <see cref="Role"/>
    /// for those legacy rows.
    /// </para>
    /// </summary>
    public MessageOrigin? Origin { get; set; }

    public required string Content { get; set; }

    /// <summary>
    /// Hidden provider-specific transcript state that may be required to
    /// faithfully replay this message to its originating model provider.
    /// This is not user-visible message content.
    /// </summary>
    public string? ProviderMetadataJson { get; set; }

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

    // ── Permission role snapshot ──────────────────────────────────

    /// <summary>
    /// Snapshot of the sender's permission role id at the time the
    /// message was sent. For a user message this is the user's role,
    /// for an assistant message this is the agent's role. Captured at
    /// send time so historical messages remain semantically correct
    /// even if the role assignment changes later.
    /// <para>
    /// Nullable: legacy rows and senders without an assigned role
    /// leave this <see langword="null"/>.
    /// </para>
    /// </summary>
    public Guid? PermissionRoleId { get; set; }

    /// <summary>
    /// Snapshot of the sender's permission role name at the time the
    /// message was sent (e.g. <c>"Admin"</c>, <c>"Default agent"</c>).
    /// Avoids a join to render history and remains accurate even if
    /// the role is renamed or reassigned later.
    /// </summary>
    public string? PermissionRoleName { get; set; }

    /// <summary>
    /// Which client interface originated this message.
    /// </summary>
    public string? ClientType { get; set; }

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
