namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Stable task-operation keys owned by the Agent Orchestration module for
/// chat/output and entity lookup/provisioning operations.
/// </summary>
public static class AgentOrchestrationOperationKeys
{
    private const string Prefix = "sharpclaw_agent_orchestration.";

    // Agent interaction

    /// <summary>Send a message to an agent and await the full response.</summary>
    public const string Chat               = Prefix + "chat";

    /// <summary>Send a message to an agent and stream the response.</summary>
    public const string ChatStream         = Prefix + "chat_stream";

    /// <summary>Send a chat message into a specific thread.</summary>
    public const string ChatToThread       = Prefix + "chat_to_thread";

    // Output

    /// <summary>Push a result object to SSE / WebSocket listeners.</summary>
    public const string Emit               = Prefix + "emit";

    /// <summary>Parse an agent text response into a typed data object.</summary>
    public const string ParseResponse      = Prefix + "parse_response";

    // Entity lookup / creation

    /// <summary>Find a model by name or custom ID.</summary>
    public const string FindModel          = Prefix + "find_model";

    /// <summary>Find a provider by name or custom ID.</summary>
    public const string FindProvider       = Prefix + "find_provider";

    /// <summary>Find an agent by name or custom ID.</summary>
    public const string FindAgent          = Prefix + "find_agent";

    /// <summary>Create a new agent.</summary>
    public const string CreateAgent        = Prefix + "create_agent";

    /// <summary>Create a new thread in a channel.</summary>
    public const string CreateThread       = Prefix + "create_thread";

    // Role / permission / channel provisioning

    /// <summary>Create a new role (upsert by name).</summary>
    public const string CreateRole         = Prefix + "create_role";

    /// <summary>Find a role by name or custom ID.</summary>
    public const string FindRole           = Prefix + "find_role";

    /// <summary>Set the permission flags on an existing role.</summary>
    public const string SetRolePermissions = Prefix + "set_role_permissions";

    /// <summary>Assign a role to an agent.</summary>
    public const string AssignRole         = Prefix + "assign_role";

    /// <summary>Create a new channel (upsert by custom ID).</summary>
    public const string CreateChannel      = Prefix + "create_channel";

    /// <summary>Find a channel by title or custom ID.</summary>
    public const string FindChannel        = Prefix + "find_channel";

    /// <summary>Add an agent to a channel's allowed agents list (idempotent).</summary>
    public const string AddAllowedAgent    = Prefix + "add_allowed_agent";
}
