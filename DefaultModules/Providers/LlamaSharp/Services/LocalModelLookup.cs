using Microsoft.EntityFrameworkCore;
using SharpClaw.Modules.Providers.LlamaSharp.LocalModels;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

/// <summary>
/// Module-internal lookup over the LlamaSharp-owned
/// <see cref="LlamaSharpDbContext"/>. Exposes the read surface other
/// default modules (Transcription) need via
/// <see cref="ILocalModelFileLookup"/>, and supplies the source URL
/// used by the LlamaSharp plugin's agent-suffix synthesis.
/// </summary>
public sealed class LocalModelLookup(LlamaSharpDbContext db) : ILocalModelFileLookup
{
    public async Task<string?> GetReadyFilePathAsync(Guid modelId, CancellationToken ct = default)
    {
        var file = await db.LocalModelFiles
            .Where(f => f.ModelId == modelId && f.Status == LocalModelStatus.Ready)
            .OrderByDescending(f => f.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        return file?.FilePath;
    }

    public async Task<string?> GetSourceUrlAsync(Guid modelId, CancellationToken ct = default)
    {
        return await db.LocalModelFiles
            .Where(f => f.ModelId == modelId)
            .Select(f => f.SourceUrl)
            .FirstOrDefaultAsync(ct);
    }
}

