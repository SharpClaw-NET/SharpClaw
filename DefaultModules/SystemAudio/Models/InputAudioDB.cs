using SharpClaw.Contracts.Entities;

namespace SharpClaw.Modules.SystemAudio.Models;

/// <summary>
/// A registered audio input source that agents can use for live transcription.
/// Module-owned copy; SkillId stored as bare <see cref="Guid"/>.
/// </summary>
public class InputAudioDB : BaseEntity
{
    public required string Name { get; set; }
    public string? DeviceIdentifier { get; set; }
    public string? Description { get; set; }
    public Guid? SkillId { get; set; }
}
