using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core;

public class ProviderDB : BaseEntity
{
    public required string Name { get; set; }
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the provider API. Only used for <c>custom</c> providers.
    /// </summary>
    public string? ApiEndpoint { get; set; }

    [HeaderSensitive]
    public string? EncryptedApiKey { get; set; }

    public ICollection<ModelDB> Models { get; set; } = [];
}
