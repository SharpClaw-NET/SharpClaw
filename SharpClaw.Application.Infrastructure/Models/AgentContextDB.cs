using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

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

    public ICollection<ConversationDB> Conversations { get; set; } = [];
    public ICollection<ScheduledTaskDB> Tasks { get; set; } = [];
    public ICollection<ContextPermissionGrantDB> PermissionGrants { get; set; } = [];
}
