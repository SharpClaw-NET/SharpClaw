using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Conversation;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.Entities;
using SharpClaw.Infrastructure.Models;

namespace SharpClaw.Application.Infrastructure.Models.Context;

/// <summary>
/// Groups conversations and tasks under a shared set of pre-authorised
/// permissions.  Context-level permission grants apply automatically to
/// every conversation and task within the context unless overridden by a
/// per-conversation or per-task grant.
/// </summary>
public class AgentContextDB : BaseEntity
{
    public required string Name { get; set; }

    public Guid AgentId { get; set; }
    public AgentDB Agent { get; set; } = null!;

    /// <summary>
    /// Optional permission set for this context. Applies automatically to
    /// every conversation and task within the context unless overridden.
    /// </summary>
    public Guid? PermissionSetId { get; set; }
    public PermissionSetDB? PermissionSet { get; set; }

    public ICollection<ConversationDB> Conversations { get; set; } = [];
    public ICollection<ScheduledJobDB> Tasks { get; set; } = [];
}
