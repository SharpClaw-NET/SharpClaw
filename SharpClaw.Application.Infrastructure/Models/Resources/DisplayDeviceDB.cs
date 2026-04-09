using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered display / monitor that agents can capture screenshots
/// from via the Computer Use module.
/// </summary>
public class DisplayDeviceDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>OS-level device identifier (e.g. <c>\\.\DISPLAY1</c> on Windows).</summary>
    public string? DeviceIdentifier { get; set; }

    /// <summary>Display index (0-based). Useful for multi-monitor setups.</summary>
    public int DisplayIndex { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    }
