namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Provides short-ID ↔ GUID resolution and JSON output for module CLI handlers.
/// Registered by the host; modules resolve via <see cref="IServiceProvider"/>.
/// </summary>
public interface ICliIdResolver
{
    /// <summary>
    /// Parses a CLI argument as either a short integer ID (with or without
    /// the <c>#</c> prefix) or a full GUID string.
    /// </summary>
    Guid Resolve(string arg);

    /// <summary>
    /// Returns the short ID for a GUID, assigning a new one if not yet mapped.
    /// </summary>
    int GetOrAssign(Guid guid);

    /// <summary>
    /// Serializes a value to JSON with short-ID injection and writes it to the console.
    /// </summary>
    void PrintJson(object value);
}
