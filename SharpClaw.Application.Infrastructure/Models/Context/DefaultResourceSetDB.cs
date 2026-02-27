using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Context;

/// <summary>
/// Stores default resource IDs for each per-resource action type.
/// Attached to a channel or context so that jobs submitted without an
/// explicit resource ID automatically use the configured default.
/// <para>
/// This is independent of <see cref="Clearance.PermissionSetDB"/> —
/// permission sets control <em>what you're allowed to do</em>, while
/// this entity controls <em>which resource to use by default</em>.
/// </para>
/// </summary>
public class DefaultResourceSetDB : BaseEntity
{
    // ── Per-resource defaults (direct resource IDs) ───────────────

    /// <summary>Default SystemUser for UnsafeExecuteAsDangerousShell.</summary>
    public Guid? DangerousShellResourceId { get; set; }

    /// <summary>Default Container for ExecuteAsSafeShell.</summary>
    public Guid? SafeShellResourceId { get; set; }

    /// <summary>Default Container for AccessContainer.</summary>
    public Guid? ContainerResourceId { get; set; }

    /// <summary>Default Website for AccessWebsite.</summary>
    public Guid? WebsiteResourceId { get; set; }

    /// <summary>Default SearchEngine for QuerySearchEngine.</summary>
    public Guid? SearchEngineResourceId { get; set; }

    /// <summary>Default LocalInformationStore for AccessLocalInfoStore.</summary>
    public Guid? LocalInfoStoreResourceId { get; set; }

    /// <summary>Default ExternalInformationStore for AccessExternalInfoStore.</summary>
    public Guid? ExternalInfoStoreResourceId { get; set; }

    /// <summary>Default AudioDevice for transcription jobs.</summary>
    public Guid? AudioDeviceResourceId { get; set; }

    /// <summary>Default Agent for ManageAgent.</summary>
    public Guid? AgentResourceId { get; set; }

    /// <summary>Default ScheduledTask for EditTask.</summary>
    public Guid? TaskResourceId { get; set; }

    /// <summary>Default Skill for AccessSkill.</summary>
    public Guid? SkillResourceId { get; set; }

    // ── Non-resource defaults ─────────────────────────────────────

    /// <summary>
    /// Default transcription model so transcription jobs don't need
    /// <c>--model</c> every time.
    /// </summary>
    public Guid? TranscriptionModelId { get; set; }
}
