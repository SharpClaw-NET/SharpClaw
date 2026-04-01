using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models;

/// <summary>
/// A named, reusable set of booleans controlling which tool-call schemas
/// are included in model requests.  When attached to an agent or channel,
/// only tools whose key maps to <see langword="true"/> (or whose key is
/// absent — absent = enabled by default) are sent.  This drastically
/// reduces prompt-token overhead for agents that only need a small
/// subset of capabilities (e.g. chat-only, transcription-only).
/// <para>
/// Override chain: channel's set → agent's set → null (all tools).
/// </para>
/// </summary>
public class ToolAwarenessSetDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Keys are canonical tool names (e.g. <c>"execute_mk8_shell"</c>,
    /// <c>"editor_read_file"</c>).  Value <see langword="true"/> means
    /// the tool schema is included; <see langword="false"/> means it is
    /// excluded.  Tools not present in the dictionary are <b>included</b>
    /// by default.
    /// </summary>
    public Dictionary<string, bool> Tools { get; set; } = new();
}
