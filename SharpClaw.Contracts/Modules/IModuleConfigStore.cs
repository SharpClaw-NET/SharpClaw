namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Per-module persistent key-value configuration store.
/// Modules resolve this from DI — the host provides a scoped instance
/// bound to the calling module's ID.
/// </summary>
public interface IModuleConfigStore
{
    /// <summary>Get a configuration value by key. Returns null if not set.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Get a typed configuration value. Returns default(T) if not set or unparseable.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : IParsable<T>;

    /// <summary>Set a configuration value. Pass null to delete the key.</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>Get all configuration keys and values for this module.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}
