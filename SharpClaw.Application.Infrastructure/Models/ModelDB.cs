using SharpClaw.Contracts.Entities;

namespace SharpClaw.Infrastructure.Models;

public class ModelDB : BaseEntity
{
    public required string Name { get; set; }

    public Guid ProviderId { get; set; }
    public ProviderDB Provider { get; set; } = null!;

    public ICollection<AgentDB> Agents { get; set; } = [];
    public ICollection<ConversationDB> Conversations { get; set; } = [];
}
