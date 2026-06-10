using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.Providers.LlamaSharp.LocalModels;
using SharpClaw.Providers.LocalCommon;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

public sealed class LocalModelStore
{
    private const string ModuleId = "sharpclaw_providers_llamasharp";
    private const string StorageName = "local_models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ModuleDocumentStore<LocalModelFileRecord> _store;

    public LocalModelStore(IModuleStorageGateway storageGateway)
    {
        _store = new ModuleDocumentStore<LocalModelFileRecord>(
            storageGateway,
            ModuleId,
            StorageName,
            JsonOptions);
    }

    public async Task<LocalModelFileRecord?> GetByModelIdAsync(
        Guid modelId,
        CancellationToken ct = default) =>
        (await RecordsByModelIdAsync(modelId, ct)).FirstOrDefault();

    public async Task<LocalModelFileRecord?> GetReadyByModelIdAsync(
        Guid modelId,
        CancellationToken ct = default) =>
        (await RecordsByModelIdAsync(modelId, ct))
            .Where(record => record.Status == LocalModelStatus.Ready)
            .OrderByDescending(record => record.UpdatedAt)
            .FirstOrDefault();

    public async Task<IReadOnlyList<LocalModelFileRecord>> ListAsync(CancellationToken ct = default) =>
        [.. (await _store.ListAsync(ct)).OrderBy(record => record.SourceUrl, StringComparer.Ordinal)];

    public async Task<(Guid ModelId, Guid FileId)> CreateOrReuseDownloadPlaceholderAsync(
        Guid modelId,
        ResolvedModelFile target,
        string requestUrl,
        string destinationPath,
        CancellationToken ct = default)
    {
        var records = await RecordsByModelIdAsync(modelId, ct);
        var now = DateTimeOffset.UtcNow;
        var existing = records.FirstOrDefault();
        if (existing is null)
        {
            existing = new LocalModelFileRecord(
                Id: Guid.NewGuid(),
                ModelId: modelId,
                SourceUrl: requestUrl,
                FilePath: destinationPath,
                FileSizeBytes: 0,
                Sha256Hash: null,
                Quantization: target.Quantization,
                Status: LocalModelStatus.Downloading,
                DownloadProgress: 0.0,
                ActivePort: null,
                MmprojPath: null,
                CreatedAt: now,
                UpdatedAt: now);
        }
        else if (existing.Status == LocalModelStatus.Downloading)
        {
            throw new InvalidOperationException(
                $"A download for model '{modelId}' is already in progress.");
        }
        else
        {
            existing = existing with
            {
                SourceUrl = requestUrl,
                FilePath = destinationPath,
                FileSizeBytes = 0,
                Quantization = target.Quantization,
                Status = LocalModelStatus.Downloading,
                DownloadProgress = 0.0,
                UpdatedAt = now,
            };
        }

        await SaveAsync(existing, ct);
        return (modelId, existing.Id);
    }

    public async Task MarkDownloadFailedAsync(Guid fileId, CancellationToken ct = default)
    {
        var record = await GetByFileIdAsync(fileId, ct);
        if (record is null)
            return;

        await SaveAsync(record with
        {
            Status = LocalModelStatus.Failed,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public async Task UpdateDownloadProgressAsync(
        Guid fileId,
        double progress,
        CancellationToken ct = default)
    {
        var record = await GetByFileIdAsync(fileId, ct);
        if (record is null)
            return;

        await SaveAsync(record with
        {
            DownloadProgress = Math.Clamp(progress, 0.0, 1.0),
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public async Task<LocalModelFileRecord> FinaliseDownloadAsync(
        Guid fileId,
        ResolvedModelFile target,
        string destinationPath,
        long fileSizeBytes,
        CancellationToken ct = default)
    {
        var record = await GetByFileIdAsync(fileId, ct)
            ?? throw new InvalidOperationException($"Local model file '{fileId}' was not found.");

        record = record with
        {
            FilePath = destinationPath,
            FileSizeBytes = fileSizeBytes,
            Quantization = target.Quantization,
            Status = LocalModelStatus.Ready,
            DownloadProgress = 1.0,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveAsync(record, ct);
        return record;
    }

    public async Task SetMmprojPathAsync(
        Guid modelId,
        string? mmprojPath,
        CancellationToken ct = default)
    {
        var record = await GetByModelIdAsync(modelId, ct)
            ?? throw new ArgumentException("No local file found for this model.");

        await SaveAsync(record with
        {
            MmprojPath = mmprojPath,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public async Task<bool> DeleteByModelIdAsync(Guid modelId, CancellationToken ct = default)
    {
        var records = await RecordsByModelIdAsync(modelId, ct);
        var deleted = false;
        foreach (var record in records)
            deleted |= await _store.DeleteAsync(Key(record.Id), ct);

        return deleted;
    }

    private async Task<IReadOnlyList<LocalModelFileRecord>> RecordsByModelIdAsync(
        Guid modelId,
        CancellationToken ct) =>
        await _store.Query()
            .WhereIndex("modelId").EqualTo(modelId.ToString("N"))
            .ToListAsync(ct);

    private Task<LocalModelFileRecord?> GetByFileIdAsync(Guid fileId, CancellationToken ct) =>
        _store.GetAsync(Key(fileId), ct);

    private Task SaveAsync(LocalModelFileRecord record, CancellationToken ct) =>
        _store.UpsertAsync(
            Key(record.Id),
            record,
            new
            {
                modelId = record.ModelId.ToString("N"),
                status = record.Status.ToString(),
                sourceUrl = record.SourceUrl,
                updatedAt = record.UpdatedAt,
            },
            ct);

    private static string Key(Guid id) => id.ToString("N");
}

public sealed record LocalModelFileRecord(
    Guid Id,
    Guid ModelId,
    string SourceUrl,
    string FilePath,
    long FileSizeBytes,
    string? Sha256Hash,
    string? Quantization,
    LocalModelStatus Status,
    double DownloadProgress,
    int? ActivePort,
    string? MmprojPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
