using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered native desktop application that agents can launch.
/// Access is per-resource via <see cref="NativeApplicationAccessDB"/>.
/// </summary>
public class NativeApplicationDB : BaseEntity
{
    /// <summary>Display name (e.g. "Microsoft Excel", "Notepad").</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path to the executable.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Optional well-known alias (e.g. "excel", "notepad").
    /// Agents can use this short name instead of the full path.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>Optional description for the agent.</summary>
    public string? Description { get; set; }

    }
