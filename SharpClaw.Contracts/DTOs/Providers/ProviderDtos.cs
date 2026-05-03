using SharpClaw.Contracts.Providers;

namespace SharpClaw.Contracts.DTOs.Providers;

/// <param name="ApiEndpoint">Required for <c>custom</c> providers. Ignored for built-in types.</param>
public sealed record CreateProviderRequest(string Name, string ProviderKey, string? ApiEndpoint = null, string? ApiKey = null);
public sealed record UpdateProviderRequest(string? Name = null, string? ApiEndpoint = null);
public sealed record SetApiKeyRequest(string ApiKey);
public sealed record ProviderResponse(Guid Id, string Name, string ProviderKey, string? ApiEndpoint, bool HasApiKey);
