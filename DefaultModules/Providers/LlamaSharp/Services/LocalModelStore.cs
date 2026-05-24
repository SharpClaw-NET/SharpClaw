using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.Providers.LlamaSharp.LocalModels;
using SharpClaw.Providers.LocalCommon;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

public sealed class LocalModelStore(IModuleConfigStore configStore)
{
    private const string StoreKey = "local_models.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<LocalModelFileRecord?> GetByModelIdAsync(
        Guid modelId,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        return records.FirstOrDefault(record => record.ModelId == modelId);
    }

    public async Task<LocalModelFileRecord?> GetReadyByModelIdAsync(
        Guid modelId,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        return records
            .Where(record => record.ModelId == modelId && record.Status == LocalModelStatus.Ready)
            .OrderByDescending(record => record.UpdatedAt)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<LocalModelFileRecord>> ListAsync(CancellationToken ct = default) =>
        [.. (await LoadAsync(ct)).OrderBy(record => record.SourceUrl, StringComparer.Ordinal)];

    public async Task<(Guid ModelId, Guid FileId)> CreateOrReuseDownloadPlaceholderAsync(
        Guid modelId,
        ResolvedModelFile target,
        string requestUrl,
        string destinationPath,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var existing = records.FirstOrDefault(record => record.ModelId == modelId);
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
            records.Add(existing);
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
            Replace(records, existing);
        }

        await SaveAsync(records, ct);
        return (modelId, existing.Id);
    }

    public async Task MarkDownloadFailedAsync(Guid fileId, CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var record = records.FirstOrDefault(candidate => candidate.Id == fileId);
        if (record is null)
            return;

        Replace(records, record with
        {
            Status = LocalModelStatus.Failed,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SaveAsync(records, ct);
    }

    public async Task UpdateDownloadProgressAsync(
        Guid fileId,
        double progress,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var record = records.FirstOrDefault(candidate => candidate.Id == fileId);
        if (record is null)
            return;

        Replace(records, record with
        {
            DownloadProgress = Math.Clamp(progress, 0.0, 1.0),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SaveAsync(records, ct);
    }

    public async Task<LocalModelFileRecord> FinaliseDownloadAsync(
        Guid fileId,
        ResolvedModelFile target,
        string destinationPath,
        long fileSizeBytes,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var record = records.FirstOrDefault(candidate => candidate.Id == fileId)
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
        Replace(records, record);
        await SaveAsync(records, ct);
        return record;
    }

    public async Task SetMmprojPathAsync(
        Guid modelId,
        string? mmprojPath,
        CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var record = records.FirstOrDefault(candidate => candidate.ModelId == modelId)
            ?? throw new ArgumentException("No local file found for this model.");

        Replace(records, record with
        {
            MmprojPath = mmprojPath,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SaveAsync(records, ct);
    }

    public async Task<bool> DeleteByModelIdAsync(Guid modelId, CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var removed = records.RemoveAll(record => record.ModelId == modelId) > 0;
        if (removed)
            await SaveAsync(records, ct);
        return removed;
    }

    private async Task<List<LocalModelFileRecord>> LoadAsync(CancellationToken ct)
    {
        var json = await configStore.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<LocalModelFileRecord>>(json, JsonOptions) ?? [];
    }

    private async Task SaveAsync(List<LocalModelFileRecord> records, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(records, JsonOptions);
        await configStore.SetAsync(StoreKey, json, ct);
    }

    private static void Replace(List<LocalModelFileRecord> records, LocalModelFileRecord record)
    {
        var index = records.FindIndex(candidate => candidate.Id == record.Id);
        if (index < 0)
            records.Add(record);
        else
            records[index] = record;
    }
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
