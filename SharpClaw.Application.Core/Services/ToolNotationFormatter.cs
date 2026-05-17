using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Application.Services;

/// <summary>
/// Single source of truth for the persisted tool-call notation appended
/// to assistant message content (see the "TOOL CALL NOTATION" block in
/// the repo's <c>copilot-instructions.md</c>).
/// <para>
/// The notation is part of the wire/storage contract — clients receive
/// it via SSE and history reads, and tests grep for the glyphs — so the
/// format strings live here instead of being inlined wherever a
/// <see cref="ChatService"/>-style call site happens to assemble them.
/// </para>
/// </summary>
public static class ToolNotationFormatter
{
    /// <summary>
    /// Glyph used for a regular (non-approval) tool execution line.
    /// </summary>
    public const string ExecutionGlyph = "⚙";

    /// <summary>
    /// Glyph used for the awaiting-approval intermediate state.
    /// </summary>
    public const string AwaitingApprovalGlyph = "⏳";

    /// <summary>
    /// Status text appended to inline / task-tool notations that don't
    /// flow through the job pipeline.
    /// </summary>
    public const string DoneStatus = "done";

    /// <summary>
    /// Action-key fallback used when a job has no <see cref="AgentJobResponse.ActionKey"/>.
    /// </summary>
    public const string UnknownAction = "unknown";

    /// <summary>
    /// Format: <c>\n⚙ [ActionKey] → Status</c> — used after a job has
    /// been submitted and executed (no approval flow).
    /// </summary>
    public static string ForJob(AgentJobResponse job)
        => $"\n{ExecutionGlyph} [{job.ActionKey ?? UnknownAction}] → {job.Status}";

    /// <summary>
    /// Format: <c>\n⏳ [ActionKey] awaiting approval → Status</c> — used
    /// for jobs paused on the approval gate.
    /// </summary>
    public static string ForApproval(AgentJobResponse job)
        => $"\n{AwaitingApprovalGlyph} [{job.ActionKey ?? UnknownAction}] awaiting approval → {job.Status}";

    /// <summary>
    /// Format: <c>\n⚙ [tool_name] → done</c> — used for inline tools
    /// (wait, list_accessible_threads, …) that bypass the job pipeline.
    /// </summary>
    public static string ForInlineTool(string toolName)
        => $"\n{ExecutionGlyph} [{toolName}] → {DoneStatus}";

    /// <summary>
    /// Format: <c>\n⚙ [tool_name] → done</c> — used for task-specific
    /// task tools surfaced by modules.
    /// </summary>
    public static string ForTaskTool(string toolName)
        => $"\n{ExecutionGlyph} [{toolName}] → {DoneStatus}";
}
