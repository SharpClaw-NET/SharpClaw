using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// Represents a registered editor session that agents can interact with
/// (read files, apply edits, run builds, etc.).  Corresponds to a
/// connected IDE instance (VS 2026, VS Code).
/// </summary>
public class EditorSessionDB : BaseEntity
{
    public string Name { get; set; } = "";
    public EditorType EditorType { get; set; }
    public string? EditorVersion { get; set; }
    public string? WorkspacePath { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// The WebSocket connection ID for the currently connected editor.
    /// Null when no editor is connected.
    /// </summary>
    public string? ConnectionId { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
