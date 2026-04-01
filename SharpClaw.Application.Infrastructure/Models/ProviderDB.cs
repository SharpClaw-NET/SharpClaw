using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

public class ProviderDB : BaseEntity
{
    public required string Name { get; set; }
    public required ProviderType ProviderType { get; set; }

    /// <summary>
    /// Base URL for the provider API. Only used for <see cref="ProviderType.Custom"/> providers.
    /// </summary>
    public string? ApiEndpoint { get; set; }

    [HeaderSensitive]
    public string? EncryptedApiKey { get; set; }

    public ICollection<ModelDB> Models { get; set; } = [];
}
