namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Stable well-known string keys for all core task step operations.
/// These constants are the authoritative discriminators in
/// <see cref="TaskStepDefinition.StepKey"/> for steps owned by the core.
/// Module authors should use namespaced keys of the form
/// <c>moduleid.step_name</c> (e.g. <c>sharpclaw.transcription.start_transcription</c>).
/// </summary>
public static class WellKnownTaskStepKeys
{
    // ── Agent interaction ──────────────────────────────────────────────────────

    /// <summary>Send a message to an agent and await the full response.</summary>
    public const string Chat           = "core.chat";

    /// <summary>Send a message to an agent and stream the response.</summary>
    public const string ChatStream     = "core.chat_stream";

    // ── Output ────────────────────────────────────────────────────────────────

    /// <summary>Push a result object to SSE / WebSocket listeners.</summary>
    public const string Emit           = "core.emit";

    /// <summary>Parse an agent text response into a typed data object.</summary>
    public const string ParseResponse  = "core.parse_response";

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Declare a local variable, optionally with an initializer.</summary>
    public const string DeclareVariable = "core.declare_variable";

    /// <summary>Assign a value to an existing variable or property.</summary>
    public const string Assign         = "core.assign";

    // ── Control flow ──────────────────────────────────────────────────────────

    /// <summary>Register a callback for an event trigger.</summary>
    public const string EventHandler   = "core.event_handler";

    /// <summary>Conditional if/else branch.</summary>
    public const string Conditional    = "core.conditional";

    /// <summary>Loop (while or foreach).</summary>
    public const string Loop           = "core.loop";

    /// <summary>Await a fixed delay.</summary>
    public const string Delay          = "core.delay";

    /// <summary>Block until the task is cancelled externally.</summary>
    public const string WaitUntilStopped = "core.wait_until_stopped";

    /// <summary>Return / exit from the task entry point.</summary>
    public const string Return         = "core.return";

    // ── HTTP ──────────────────────────────────────────────────────────────────

    /// <summary>Make an HTTP request.</summary>
    public const string HttpRequest    = "core.http_request";

    // ── Evaluation ────────────────────────────────────────────────────────────

    /// <summary>Evaluate a restricted C# expression.</summary>
    public const string Evaluate       = "core.evaluate";

    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>Write a log message.</summary>
    public const string Log            = "core.log";

    // ── Entity lookup / creation ──────────────────────────────────────────────

    /// <summary>Find a model by name or custom ID.</summary>
    public const string FindModel      = "core.find_model";

    /// <summary>Find a provider by name or custom ID.</summary>
    public const string FindProvider   = "core.find_provider";

    /// <summary>Find an agent by name or custom ID.</summary>
    public const string FindAgent      = "core.find_agent";

    /// <summary>Create a new agent.</summary>
    public const string CreateAgent    = "core.create_agent";

    /// <summary>Create a new thread in a channel.</summary>
    public const string CreateThread   = "core.create_thread";

    /// <summary>Send a chat message into a specific thread.</summary>
    public const string ChatToThread   = "core.chat_to_thread";

    // ── Role / permission / channel provisioning ──────────────────────────────

    /// <summary>Create a new role (upsert by name).</summary>
    public const string CreateRole     = "core.create_role";

    /// <summary>Find a role by name or custom ID.</summary>
    public const string FindRole       = "core.find_role";

    /// <summary>Set the permission flags on an existing role.</summary>
    public const string SetRolePermissions = "core.set_role_permissions";

    /// <summary>Assign a role to an agent.</summary>
    public const string AssignRole     = "core.assign_role";

    /// <summary>Create a new channel (upsert by custom ID).</summary>
    public const string CreateChannel  = "core.create_channel";

    /// <summary>Find a channel by title or custom ID.</summary>
    public const string FindChannel    = "core.find_channel";

    /// <summary>Add an agent to a channel's allowed agents list (idempotent).</summary>
    public const string AddAllowedAgent = "core.add_allowed_agent";
}
