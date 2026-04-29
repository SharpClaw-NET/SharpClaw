namespace SharpClaw.Modules.SystemAudio.DTOs;

public sealed record CreateInputAudioRequest(
    string Name,
    string? DeviceIdentifier = null,
    string? Description = null);

public sealed record UpdateInputAudioRequest(
    string? Name = null,
    string? DeviceIdentifier = null,
    string? Description = null);

public sealed record InputAudioResponse(
    Guid Id,
    string Name,
    string? DeviceIdentifier,
    string? Description,
    Guid? SkillId,
    DateTimeOffset CreatedAt);

public sealed record InputAudioSyncResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> ImportedNames,
    IReadOnlyList<string> SkippedNames);
