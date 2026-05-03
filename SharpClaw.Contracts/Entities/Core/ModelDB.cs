using System.ComponentModel.DataAnnotations.Schema;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core;

public class ModelDB : BaseEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Comma-separated capability tags (e.g. "chat,vision", "embedding", "tts").
    /// Use <see cref="WellKnownCapabilityKeys"/> for well-known values.
    /// </summary>
    public string? CapabilityTagsRaw { get; set; }

    /// <summary>
    /// Parsed view of <see cref="CapabilityTagsRaw"/>.
    /// </summary>
    [NotMapped]
    public IReadOnlySet<string> CapabilityTags =>
        string.IsNullOrEmpty(CapabilityTagsRaw)
            ? (IReadOnlySet<string>)new HashSet<string>()
            : new HashSet<string>(CapabilityTagsRaw.Split(','), StringComparer.OrdinalIgnoreCase);

    public Guid ProviderId { get; set; }
    public ProviderDB Provider { get; set; } = null!;

    public ICollection<AgentDB> Agents { get; set; } = [];
}
