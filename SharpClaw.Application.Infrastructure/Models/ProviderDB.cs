using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Infrastructure.Models;

public class ProviderDB : BaseEntity
{
    public required string Name { get; set; }
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the provider API. Only used for <see cref="WellKnownProviderKeys.Custom"/> providers.
    /// </summary>
    public string? ApiEndpoint { get; set; }

    [HeaderSensitive]
    public string? EncryptedApiKey { get; set; }

    public ICollection<ModelDB> Models { get; set; } = [];
}
