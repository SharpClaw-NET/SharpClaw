namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Stable well-known step keys owned by the Task Scripting module: scripting
/// language primitives (declare/assign/conditional/loop/return/event_handler/
/// evaluate) and runtime-control primitives (delay/wait_until_stopped/log).
/// <para>
/// IMPORTANT: The literal string values intentionally match the legacy
/// <c>core.*</c> values so existing serialized task scripts continue to parse.
/// Only the C# location of the constants changes; the wire format does not.
/// </para>
/// </summary>
public static class TaskScriptingStepKeys
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Declare a local variable, optionally with an initializer.</summary>
    public const string DeclareVariable  = "core.declare_variable";

    /// <summary>Assign a value to an existing variable or property.</summary>
    public const string Assign           = "core.assign";

    // ── Control flow ──────────────────────────────────────────────────────────

    /// <summary>Register a callback for an event trigger.</summary>
    public const string EventHandler     = "core.event_handler";

    /// <summary>Conditional if/else branch.</summary>
    public const string Conditional      = "core.conditional";

    /// <summary>Loop (while or foreach).</summary>
    public const string Loop             = "core.loop";

    /// <summary>Return / exit from the task entry point.</summary>
    public const string Return           = "core.return";

    /// <summary>Evaluate a restricted C# expression.</summary>
    public const string Evaluate         = "core.evaluate";

    // ── Runtime control ───────────────────────────────────────────────────────

    /// <summary>Await a fixed delay.</summary>
    public const string Delay            = "core.delay";

    /// <summary>Block until the task is cancelled externally.</summary>
    public const string WaitUntilStopped = "core.wait_until_stopped";

    /// <summary>Write a log message.</summary>
    public const string Log              = "core.log";

    // ── Response shaping ──────────────────────────────────────────────────────

    /// <summary>Parse an agent text response into a typed data object.</summary>
    /// <remarks>
    /// The runtime semantics live in the Agent Orchestration module
    /// (<c>AgentOrchestrationStepKeys.ParseResponse</c> binds the same wire
    /// value to <c>ParseResponse&lt;T&gt;()</c> calls on the context API).
    /// This entry exists because the parser may emit the same step key
    /// from a statement shape, and the validator inspects it to verify the
    /// referenced type is known to the task.
    /// </remarks>
    public const string ParseResponse    = "core.parse_response";
}
