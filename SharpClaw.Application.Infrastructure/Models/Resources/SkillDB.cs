using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// AI-readable instruction text that describes how to interact with
/// a particular resource (website, search engine, container, etc.).
/// Attached to resource entities so agents can look up usage guidance
/// at runtime.
/// </summary>
public class SkillDB : BaseEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The full instruction / how-to text the AI consumes when it needs
    /// to use the associated resource.
    /// </summary>
    public required string SkillText { get; set; }
}
