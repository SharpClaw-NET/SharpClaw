using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

/// <summary>
/// Module-side implementation of <see cref="ILocalModelLookup"/> that
/// queries the LlamaSharp-owned <see cref="LlamaSharpDbContext"/>.
/// </summary>
public sealed class LocalModelLookup(LlamaSharpDbContext db) : ILocalModelLookup
{
    public async Task<string?> GetReadyFilePathAsync(Guid modelId, CancellationToken ct = default)
    {
        var file = await db.LocalModelFiles
            .Where(f => f.ModelId == modelId && f.Status == LocalModelStatus.Ready)
            .OrderByDescending(f => f.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        return file?.FilePath;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetSourceUrlsForModelsAsync(
        IEnumerable<Guid> modelIds, CancellationToken ct = default)
    {
        var ids = modelIds as IReadOnlyCollection<Guid> ?? modelIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.LocalModelFiles
            .Where(f => ids.Contains(f.ModelId))
            .ToDictionaryAsync(f => f.ModelId, f => f.SourceUrl, ct);
    }
}
