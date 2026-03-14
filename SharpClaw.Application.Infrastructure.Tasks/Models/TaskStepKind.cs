namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// Closed set of operations a task step can perform.  Every statement
/// in a task script entry-point body maps to exactly one kind.
/// </summary>
public enum TaskStepKind
{
    // ── Agent interaction ─────────────────────────────────────────

    /// <summary>Send a message to an agent and await the full response.</summary>
    Chat,

    /// <summary>Send a message to an agent and stream the response.</summary>
    ChatStream,

    // ── Transcription ─────────────────────────────────────────────

    /// <summary>Begin a live transcription job on an audio device.</summary>
    StartTranscription,

    /// <summary>Stop a running transcription job.</summary>
    StopTranscription,

    /// <summary>Resolve the system default audio device.</summary>
    GetDefaultAudioDevice,

    // ── Output ────────────────────────────────────────────────────

    /// <summary>Push a result object to SSE / WebSocket listeners.</summary>
    Emit,

    /// <summary>Parse an agent text response into a typed data object.</summary>
    ParseResponse,

    // ── State ─────────────────────────────────────────────────────

    /// <summary>Declare a local variable, optionally with an initializer.</summary>
    DeclareVariable,

    /// <summary>Assign a value to an existing variable or property.</summary>
    Assign,

    // ── Control flow ──────────────────────────────────────────────

    /// <summary>Register a callback for an event trigger.</summary>
    EventHandler,

    /// <summary>Conditional if/else branch.</summary>
    Conditional,

    /// <summary>Loop (while or foreach).</summary>
    Loop,

    /// <summary>Await a fixed delay.</summary>
    Delay,

    /// <summary>Block until the task is cancelled externally.</summary>
    WaitUntilStopped,

    /// <summary>Return / exit from the task entry point.</summary>
    Return,

    // ── HTTP ──────────────────────────────────────────────────────

    /// <summary>Make an HTTP request.</summary>
    HttpRequest,

    // ── Evaluation ────────────────────────────────────────────────

    /// <summary>Evaluate a restricted C# expression.</summary>
    Evaluate,

    // ── Logging ───────────────────────────────────────────────────

    /// <summary>Write a log message.</summary>
    Log,

    // ── Entity lookup / creation ──────────────────────────────────

    /// <summary>Find a model by name or custom ID.</summary>
    FindModel,

    /// <summary>Find a provider by name or custom ID.</summary>
    FindProvider,

    /// <summary>Find an agent by name or custom ID.</summary>
    FindAgent,

    /// <summary>Create a new agent.</summary>
    CreateAgent,

    /// <summary>Create a new thread in a channel.</summary>
    CreateThread,

    /// <summary>Send a chat message into a specific thread.</summary>
    ChatToThread,
}
