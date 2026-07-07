namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Provides protected secret reads to in-process modules running inside the
/// SharpClaw host process. This interface is intentionally not exposed through
/// the foreign-module protocol.
/// </summary>
public interface IInProcessModuleSecretReader
{
    Task<string?> GetProviderApiKeyAsync(
        string providerKey, CancellationToken ct = default);

    Task<string?> GetModelProviderApiKeyAsync(
        Guid modelId, CancellationToken ct = default);
}
