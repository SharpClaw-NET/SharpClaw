namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

/// <summary>
/// Module-owned read surface for resolving the on-disk path of a
/// local model's most recent <c>Ready</c> file. Consumed by other
/// default modules (currently Transcription) that need a local model
/// file path without depending on Core.
/// </summary>
public interface ILocalModelFileLookup
{
    /// <summary>
    /// Returns the on-disk path of the most recent <c>Ready</c> file for
    /// the given model, or <c>null</c> if no Ready file exists.
    /// </summary>
    Task<string?> GetReadyFilePathAsync(Guid modelId, CancellationToken ct = default);
}
