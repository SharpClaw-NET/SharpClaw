using SharpClaw.Application.Infrastructure.Models.Access;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Resources;

/// <summary>
/// A registered audio input device that agents can use for live
/// transcription.
/// </summary>
public class AudioDeviceDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>OS-level device identifier or path (e.g. WASAPI device ID).</summary>
    public string? DeviceIdentifier { get; set; }

    public string? Description { get; set; }

    public Guid? SkillId { get; set; }
    public SkillDB? Skill { get; set; }

    public ICollection<AudioDeviceAccessDB> Accesses { get; set; } = [];
}
