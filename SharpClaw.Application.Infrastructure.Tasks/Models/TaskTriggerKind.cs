namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// Events that can trigger a task event-handler callback.
/// </summary>
public enum TaskTriggerKind
{
    /// <summary>Fired when a transcription job produces a new segment.</summary>
    TranscriptionSegment,

    /// <summary>Fired on a periodic timer interval.</summary>
    Timer,

    /// <summary>Fired once when the task starts executing.</summary>
    TaskStarted,

    /// <summary>Fired when the task is being stopped / cancelled.</summary>
    TaskStopping,
}
