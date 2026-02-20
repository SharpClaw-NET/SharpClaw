using SharpClaw.Application.Infrastructure.Models.Conversation;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

public class ModelDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Advertised capabilities. Defaults to <see cref="ModelCapability.Chat"/>.
    /// A transcription-capable model (e.g. Whisper) should include
    /// <see cref="ModelCapability.Transcription"/>.
    /// </summary>
    public ModelCapability Capabilities { get; set; } = ModelCapability.Chat;

    public Guid ProviderId { get; set; }
    public ProviderDB Provider { get; set; } = null!;

    public ICollection<AgentDB> Agents { get; set; } = [];
    public ICollection<ConversationDB> Conversations { get; set; } = [];
}
