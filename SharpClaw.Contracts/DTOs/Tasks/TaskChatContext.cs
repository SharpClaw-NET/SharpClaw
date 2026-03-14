namespace SharpClaw.Contracts.DTOs.Tasks;

/// <summary>
/// Context passed through <see cref="Chat.ChatRequest"/> when a chat
/// message originates from an automated task.  Carries the instance
/// identity so <see cref="SharpClaw.Application.Services.ChatService"/>
/// can look up the task's shared data store and expose task-specific
/// tools to the model.
/// </summary>
public sealed record TaskChatContext(
    /// <summary>Unique identifier of the running task instance.</summary>
    Guid InstanceId,
    /// <summary>Human-readable task name (from <c>[Task("…")]</c>).</summary>
    string TaskName);
