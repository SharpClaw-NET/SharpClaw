namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Module→host read surface for local model files. Implemented by the
/// LlamaSharp module (or any provider hosting GGUF files); consumed by
/// host services that previously queried <c>LocalModelFiles</c> directly.
/// Optional: when no module providing local inference is enabled, this
/// service may not be registered and consumers must treat absence as
/// "no local files available".
/// </summary>
public interface ILocalModelLookup
{
    /// <summary>
    /// Returns the on-disk path of the most recent <c>Ready</c> file for
    /// the given model, or <c>null</c> if no Ready file exists.
    /// </summary>
    Task<string?> GetReadyFilePathAsync(Guid modelId, CancellationToken ct = default);

    /// <summary>
    /// Returns a map of <c>modelId → sourceUrl</c> for the given models.
    /// Models without a registered file are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSourceUrlsForModelsAsync(
        IEnumerable<Guid> modelIds, CancellationToken ct = default);
}
