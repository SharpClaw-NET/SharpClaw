using SharpClaw.Modules.Providers.LlamaSharp.LocalModels;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

/// <summary>
/// Module-internal lookup over the LlamaSharp-owned local-model store. Exposes the read surface that other
/// modules can use via <see cref="ILocalModelFileLookup"/>, and supplies the source URL
/// used by the LlamaSharp plugin's agent-suffix synthesis.
/// </summary>
public sealed class LocalModelLookup(LocalModelStore store) : ILocalModelFileLookup
{
    public async Task<string?> GetReadyFilePathAsync(Guid modelId, CancellationToken ct = default)
    {
        var file = await store.GetReadyByModelIdAsync(modelId, ct);
        return file?.FilePath;
    }

    public async Task<string?> GetSourceUrlAsync(Guid modelId, CancellationToken ct = default)
    {
        var file = await store.GetByModelIdAsync(modelId, ct);
        return file?.SourceUrl;
    }
}

