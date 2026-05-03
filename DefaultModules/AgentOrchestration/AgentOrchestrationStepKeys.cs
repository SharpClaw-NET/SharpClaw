namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Stable well-known task-step keys owned by the Agent Orchestration module:
/// chat/output primitives and entity lookup/provisioning operations.
/// <para>
/// IMPORTANT: The literal string values intentionally match the legacy
/// <c>core.*</c> values so existing serialized task scripts continue to parse.
/// Only the C# location of the constants changes; the wire format does not.
/// </para>
/// </summary>
public static class AgentOrchestrationStepKeys
{
    // ── Agent interaction ─────────────────────────────────────────────────────

    /// <summary>Send a message to an agent and await the full response.</summary>
    public const string Chat               = "core.chat";

    /// <summary>Send a message to an agent and stream the response.</summary>
    public const string ChatStream         = "core.chat_stream";

    /// <summary>Send a chat message into a specific thread.</summary>
    public const string ChatToThread       = "core.chat_to_thread";

    // ── Output ────────────────────────────────────────────────────────────────

    /// <summary>Push a result object to SSE / WebSocket listeners.</summary>
    public const string Emit               = "core.emit";

    /// <summary>Parse an agent text response into a typed data object.</summary>
    public const string ParseResponse      = "core.parse_response";

    // ── Entity lookup / creation ──────────────────────────────────────────────

    /// <summary>Find a model by name or custom ID.</summary>
    public const string FindModel          = "core.find_model";

    /// <summary>Find a provider by name or custom ID.</summary>
    public const string FindProvider       = "core.find_provider";

    /// <summary>Find an agent by name or custom ID.</summary>
    public const string FindAgent          = "core.find_agent";

    /// <summary>Create a new agent.</summary>
    public const string CreateAgent        = "core.create_agent";

    /// <summary>Create a new thread in a channel.</summary>
    public const string CreateThread       = "core.create_thread";

    // ── Role / permission / channel provisioning ──────────────────────────────

    /// <summary>Create a new role (upsert by name).</summary>
    public const string CreateRole         = "core.create_role";

    /// <summary>Find a role by name or custom ID.</summary>
    public const string FindRole           = "core.find_role";

    /// <summary>Set the permission flags on an existing role.</summary>
    public const string SetRolePermissions = "core.set_role_permissions";

    /// <summary>Assign a role to an agent.</summary>
    public const string AssignRole         = "core.assign_role";

    /// <summary>Create a new channel (upsert by custom ID).</summary>
    public const string CreateChannel      = "core.create_channel";

    /// <summary>Find a channel by title or custom ID.</summary>
    public const string FindChannel        = "core.find_channel";

    /// <summary>Add an agent to a channel's allowed agents list (idempotent).</summary>
    public const string AddAllowedAgent    = "core.add_allowed_agent";
}
