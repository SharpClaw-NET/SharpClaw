namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Non-secret provider construction facts supplied by a host or provider
/// factory.
/// </summary>
public sealed record ProviderClientOptions(string Endpoint);
