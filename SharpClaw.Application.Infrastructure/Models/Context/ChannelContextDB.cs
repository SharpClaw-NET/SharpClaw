using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Context;

/// <summary>
/// Groups channels and tasks under a shared set of pre-authorised
/// permissions.  Context-level permission grants apply automatically to
/// every channel and task within the context unless overridden by a
/// per-channel or per-task grant.
/// </summary>
public class ChannelContextDB : BaseEntity
{
    public required string Name { get; set; }

    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;

    /// <summary>
    /// Optional permission set for this context. Applies automatically to
    /// every channel and task within the context unless overridden.
    /// </summary>
    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    /// <summary>
    /// Default resources for channels in this context.  A channel
    /// inherits these when its own <see cref="ChannelDB.DefaultResourceSet"/>
    /// does not specify a value for the action type.
    /// </summary>
    public Guid? DefaultResourceSetId { get; set; }
    public DefaultResourceSetDB? DefaultResourceSet { get; set; }

    /// <summary>
    /// Default value for <see cref="ChannelDB.DisableChatHeader"/> for
    /// channels inside this context. Individual channels can override.
    /// </summary>
    public bool DisableChatHeader { get; set; }

    /// <summary>
    /// Default set of additional agents allowed on channels in this
    /// context.  A channel inherits these when its own
    /// <see cref="ChannelDB.AllowedAgents"/> is empty.
    /// </summary>
    public ICollection<AgentDB> AllowedAgents { get; set; } = [];

    public ICollection<ChannelDB> Channels { get; set; } = [];
    public ICollection<ScheduledJobDB> Tasks { get; set; } = [];
}
