namespace SharpClaw.Contracts.DTOs.DefaultResources;

/// <summary>
/// Sets the default resources for a channel or context.  Each field
/// is a direct resource GUID — when a job is submitted without an
/// explicit resource the matching field is used automatically.
/// </summary>
public sealed record SetDefaultResourcesRequest(
    Guid? DangerousShellResourceId = null,
    Guid? SafeShellResourceId = null,
    Guid? ContainerResourceId = null,
    Guid? WebsiteResourceId = null,
    Guid? SearchEngineResourceId = null,
    Guid? InternalDatabaseResourceId = null,
    Guid? ExternalDatabaseResourceId = null,
    Guid? InputAudioResourceId = null,
    Guid? DisplayDeviceResourceId = null,
    Guid? AgentResourceId = null,
    Guid? TaskResourceId = null,
    Guid? SkillResourceId = null,
    Guid? TranscriptionModelId = null,
    Guid? EditorSessionResourceId = null,
    Guid? DocumentSessionResourceId = null,
    Guid? NativeApplicationResourceId = null);

public sealed record DefaultResourcesResponse(
    Guid Id,
    Guid? DangerousShellResourceId,
    Guid? SafeShellResourceId,
    Guid? ContainerResourceId,
    Guid? WebsiteResourceId,
    Guid? SearchEngineResourceId,
    Guid? InternalDatabaseResourceId,
    Guid? ExternalDatabaseResourceId,
    Guid? InputAudioResourceId,
    Guid? DisplayDeviceResourceId,
    Guid? AgentResourceId,
    Guid? TaskResourceId,
    Guid? SkillResourceId,
    Guid? TranscriptionModelId,
    Guid? EditorSessionResourceId,
    Guid? DocumentSessionResourceId,
    Guid? NativeApplicationResourceId);

/// <summary>
/// Sets a single default resource by key.
/// </summary>
public sealed record SetDefaultResourceByKeyRequest(Guid ResourceId);
