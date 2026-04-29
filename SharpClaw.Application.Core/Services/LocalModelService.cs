using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Core.Providers;
using SharpClaw.Contracts.DTOs.LocalModels;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class LocalModelService(
    SharpClawDbContext db,
    HuggingFaceUrlResolver urlResolver,
    ModelDownloadManager downloadManager,
    LocalInferenceProcessManager processManager)
{
    /// <summary>
    /// Downloads a model and registers it under an explicit provider.
    /// <paramref name="request"/>.<c>ProviderType</c> must be non-null.
    /// </summary>
    public async Task<ModelResponse> DownloadAndRegisterAsync(
        DownloadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request.ProviderKey,
            $"{nameof(request)}.{nameof(request.ProviderKey)}");

        var (provider, defaultCapability) = request.ProviderKey switch
        {
            WellKnownProviderKeys.LlamaSharp => (await EnsureLocalProviderAsync(ct), WellKnownCapabilityKeys.Chat),
            _ => throw new ArgumentException(
                $"Provider type '{request.ProviderKey}' does not support local file download. " +
                "Only LlamaSharp is supported by the host; other local providers must register via a module.",
                nameof(request))
        };

        return await DownloadAndRegisterCoreAsync(request, provider, defaultCapability, progress, ct);
    }

    private async Task<ModelResponse> DownloadAndRegisterCoreAsync(
        DownloadModelRequest request,
        ProviderDB provider,
        string defaultCapability,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Phase A: resolve the download target and persist a placeholder row
        // BEFORE starting the download. Previously the model + file rows were
        // only created after DownloadAsync returned, which meant GET
        // /models/local returned an empty list for the entire duration of a
        // multi-GB download — zero visibility into in-flight downloads. See
        // bug #2 in docs/internal/local-inference-pipeline-debug-report.md.
        var target = await ResolveDownloadTargetAsync(request, ct);
        var modelName = request.Name ?? Path.GetFileNameWithoutExtension(target.Target.Filename);

        var (modelId, fileId) = await CreateOrReuseDownloadPlaceholderAsync(
            modelName, provider, defaultCapability, target, ct);

        // Phase B: download. Wrap the caller's IProgress so progress updates
        // flow both to the caller and into the DB. DB writes are throttled
        // to whole-percent increments to avoid thrashing the persistence
        // layer on fast connections.
        var dbProgress = new ThrottledDbProgressWriter(db, fileId, progress);
        try
        {
            await downloadManager.DownloadAsync(target.DownloadUrl, target.DestPath, dbProgress, ct);
        }
        catch
        {
            // Phase C (error): flip to Failed so the row is observable as
            // a failed attempt rather than silently stuck in Downloading.
            await MarkDownloadFailedAsync(fileId, ct);
            throw;
        }

        // Phase C (success): finalise the placeholder into a Ready row.
        return await FinaliseDownloadAsync(modelId, fileId, target, ct);
    }

    /// <summary>
    /// Resolves the HuggingFace / direct URL to a concrete download target
    /// without actually downloading anything. Splits the "resolve" work out
    /// of <see cref="DownloadFileAsync"/> so callers can use the resolved
    /// target to create a placeholder DB row before the download begins.
    /// </summary>
    private async Task<ResolvedDownloadTarget> ResolveDownloadTargetAsync(
        DownloadModelRequest request,
        CancellationToken ct)
    {
        var files = await urlResolver.ResolveAsync(request.Url, ct);
        if (files.Count == 0)
            throw new ArgumentException("No downloadable GGUF files found at the URL.");

        var target = request.Quantization is not null
            ? files.FirstOrDefault(f =>
                f.Quantization?.Equals(request.Quantization, StringComparison.OrdinalIgnoreCase) == true)
              ?? files[0]
            : files[0];

        var sourceFolder = ModelDownloadManager.ResolveSourceFolder(request.Url);
        var destPath = downloadManager.GetModelPath(sourceFolder, target.Filename);
        return new ResolvedDownloadTarget(target, target.DownloadUrl, destPath, request.Url);
    }

    /// <summary>
    /// Creates the <see cref="ModelDB"/> and <see cref="LocalModelFileDB"/>
    /// rows for an in-progress download, or reuses them if a matching
    /// combination already exists (e.g. re-download after
    /// <see cref="LocalModelStatus.Failed"/>).
    /// Returns <c>(modelId, fileId)</c> so later phases don't have to re-query.
    /// </summary>
    private async Task<(Guid ModelId, Guid FileId)> CreateOrReuseDownloadPlaceholderAsync(
        string modelName,
        ProviderDB provider,
        string defaultCapability,
        ResolvedDownloadTarget target,
        CancellationToken ct)
    {
        var model = await db.Models
            .FirstOrDefaultAsync(m => m.Name == modelName && m.ProviderId == provider.Id, ct);
        if (model is null)
        {
            var tags = ProviderCapabilityHeuristics.ForGeneric(modelName);
            if (tags.Count == 0)
                tags = [defaultCapability];
            model = new ModelDB
            {
                Name = modelName,
                ProviderId = provider.Id,
                CapabilityTagsRaw = string.Join(',', tags)
            };
            db.Models.Add(model);
        }

        var file = await db.LocalModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == model.Id, ct);
        if (file is null)
        {
            file = new LocalModelFileDB
            {
                ModelId = model.Id,
                SourceUrl = target.RequestUrl,
                FilePath = target.DestPath,
                FileSizeBytes = 0,
                Quantization = target.Target.Quantization,
                Status = LocalModelStatus.Downloading,
                DownloadProgress = 0.0,
            };
            db.LocalModelFiles.Add(file);
        }
        else if (file.Status == LocalModelStatus.Downloading)
        {
            // A prior download is already in flight for this (model, provider)
            // pairing. Refuse rather than race — the caller can poll status
            // instead.
            throw new InvalidOperationException(
                $"A download for model '{modelName}' is already in progress.");
        }
        else
        {
            // Re-download after Ready or Failed: reset the row to Downloading
            // and let the fresh download overwrite the file.
            file.SourceUrl = target.RequestUrl;
            file.FilePath = target.DestPath;
            file.Quantization = target.Target.Quantization;
            file.Status = LocalModelStatus.Downloading;
            file.DownloadProgress = 0.0;
            file.FileSizeBytes = 0;
        }

        await db.SaveChangesAsync(ct);
        return (model.Id, file.Id);
    }

    private async Task MarkDownloadFailedAsync(Guid fileId, CancellationToken ct)
    {
        // Use a dedicated scope: the enclosing ct may be cancelled. Even so
        // we want the row to reflect Failed, so pass CancellationToken.None
        // for the write itself.
        var file = await db.LocalModelFiles.FirstOrDefaultAsync(f => f.Id == fileId, CancellationToken.None);
        if (file is null) return;
        file.Status = LocalModelStatus.Failed;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ModelResponse> FinaliseDownloadAsync(
        Guid modelId, Guid fileId, ResolvedDownloadTarget target, CancellationToken ct)
    {
        var model = await db.Models.FirstAsync(m => m.Id == modelId, ct);
        var file = await db.LocalModelFiles.FirstAsync(f => f.Id == fileId, ct);

        file.FilePath = target.DestPath;
        file.FileSizeBytes = new FileInfo(target.DestPath).Length;
        file.Status = LocalModelStatus.Ready;
        file.DownloadProgress = 1.0;
        file.Quantization = target.Target.Quantization;

        await db.SaveChangesAsync(ct);

        var provider = await db.Providers.FirstAsync(p => p.Id == model.ProviderId, ct);
        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name, model.CapabilityTagsRaw);
    }

    private sealed record ResolvedDownloadTarget(
        ResolvedModelFile Target, string DownloadUrl, string DestPath, string RequestUrl);

    /// <summary>
    /// Wraps the caller's <see cref="IProgress{Double}"/> (if any) and also
    /// flushes whole-percent progress updates to the persisted
    /// <see cref="LocalModelFileDB"/> row so that <c>GET /models/local</c>
    /// clients can observe download progress in real time.
    /// <para>
    /// Whole-percent throttling keeps DB write pressure bounded on fast
    /// connections. Final "100%" is not written here — callers finalise via
    /// <c>FinaliseDownloadAsync</c>.
    /// </para>
    /// </summary>
    private sealed class ThrottledDbProgressWriter(
        SharpClawDbContext db,
        Guid fileId,
        IProgress<double>? inner) : IProgress<double>
    {
        private int _lastWrittenPct = -1;

        public void Report(double value)
        {
            inner?.Report(value);

            // Clamp to [0, 1) — final 1.0 is persisted by FinaliseDownloadAsync.
            if (value < 0) value = 0;
            if (value >= 1) return;

            var pct = (int)(value * 100);
            if (pct <= _lastWrittenPct) return;
            _lastWrittenPct = pct;

            // Fire-and-forget is acceptable here: the next increment will
            // overwrite any dropped write, and the finalise step is
            // authoritative for the terminal state.
            _ = UpdateAsync(value);
        }

        private async Task UpdateAsync(double value)
        {
            try
            {
                var file = await db.LocalModelFiles
                    .FirstOrDefaultAsync(f => f.Id == fileId);
                if (file is null) return;
                file.DownloadProgress = value;
                await db.SaveChangesAsync();
            }
            catch
            {
                // Persistence errors during a long download must not abort
                // the download itself. The finalise step will settle the
                // row regardless.
            }
        }
    }

    private async Task<DownloadedFile> DownloadFileAsync(
        DownloadModelRequest request,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var files = await urlResolver.ResolveAsync(request.Url, ct);
        if (files.Count == 0)
            throw new ArgumentException("No downloadable GGUF files found at the URL.");

        var target = request.Quantization is not null
            ? files.FirstOrDefault(f =>
                f.Quantization?.Equals(request.Quantization, StringComparison.OrdinalIgnoreCase) == true)
              ?? files[0]
            : files[0];

        var sourceFolder = ModelDownloadManager.ResolveSourceFolder(request.Url);
        var destPath     = downloadManager.GetModelPath(sourceFolder, target.Filename);

        await downloadManager.DownloadAsync(target.DownloadUrl, destPath, progress, ct);

        return new DownloadedFile(destPath, request.Url, target);
    }

    private async Task<ModelResponse> RegisterModelAsync(
        DownloadedFile file,
        string modelName,
        ProviderDB provider,
        string defaultCapability,
        CancellationToken ct)
    {
        var existingModel = await db.Models
            .FirstOrDefaultAsync(m => m.Name == modelName && m.ProviderId == provider.Id, ct);

        if (existingModel is not null)
        {
            var existingFile = await db.LocalModelFiles
                .FirstOrDefaultAsync(f => f.ModelId == existingModel.Id, ct);

            if (existingFile is not null)
            {
                existingFile.FilePath        = file.DestPath;
                existingFile.FileSizeBytes   = new FileInfo(file.DestPath).Length;
                existingFile.Status          = LocalModelStatus.Ready;
                existingFile.DownloadProgress = 1.0;
                existingFile.Quantization    = file.Target.Quantization;
                await db.SaveChangesAsync(ct);
            }

            return new ModelResponse(existingModel.Id, existingModel.Name,
                provider.Id, provider.Name, existingModel.CustomId, existingModel.CapabilityTags);
        }

        var tags = ProviderCapabilityHeuristics.ForGeneric(modelName);
        if (tags.Count == 0)
            tags = [defaultCapability];

        var model = new ModelDB
        {
            Name = modelName,
            ProviderId = provider.Id,
            CapabilityTagsRaw = string.Join(',', tags)
        };
        db.Models.Add(model);

        db.LocalModelFiles.Add(new LocalModelFileDB
        {
            ModelId       = model.Id,
            SourceUrl     = file.SourceUrl,
            FilePath      = file.DestPath,
            FileSizeBytes = new FileInfo(file.DestPath).Length,
            Quantization  = file.Target.Quantization,
            Status        = LocalModelStatus.Ready,
            DownloadProgress = 1.0
        });

        await db.SaveChangesAsync(ct);
        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name, model.CustomId, model.CapabilityTags);
    }

    private sealed record DownloadedFile(string DestPath, string SourceUrl, ResolvedModelFile Target);

    /// <summary>
    /// Pins the model so it stays loaded between requests.
    /// Use <c>model load</c> CLI / <c>POST /models/local/{id}/load</c>.
    /// </summary>
    public async Task LoadModelAsync(
        Guid modelId, LoadModelRequest request, CancellationToken ct = default)
    {
        var localFile = await db.LocalModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == modelId, ct)
            ?? throw new ArgumentException("No local file found for this model.");

        if (localFile.Status != LocalModelStatus.Ready)
            throw new InvalidOperationException($"Model file status is {localFile.Status}.");

        await processManager.PinAsync(
            modelId, localFile.FilePath, request.GpuLayers, request.ContextSize,
            localFile.MmprojPath ?? request.MmprojPath, ct);
    }

    /// <summary>
    /// Unpins the model. If no chat requests are in-flight the model is
    /// unloaded immediately; otherwise it unloads when the last request completes.
    /// </summary>
    public Task UnloadModelAsync(Guid modelId, CancellationToken ct = default)
    {
        processManager.Unpin(modelId);
        return Task.CompletedTask;
    }

    // ── Chat auto-load lifecycle ──────────────────────────────────

    /// <summary>
    /// Called by <see cref="ChatService"/> before a chat request.
    /// Loads the model into memory if not already loaded and increments
    /// the reference count.
    /// </summary>
    public async Task EnsureReadyForChatAsync(Guid modelId, CancellationToken ct)
    {
        var localFile = await db.LocalModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == modelId, ct)
            ?? throw new InvalidOperationException("No local file found for this model.");

        if (localFile.Status != LocalModelStatus.Ready)
            throw new InvalidOperationException($"Model file status is {localFile.Status}.");

        await processManager.AcquireAsync(modelId, localFile.FilePath, mmprojPath: localFile.MmprojPath, ct: ct);
    }

    /// <summary>
    /// Called by <see cref="ChatService"/> after a chat request completes
    /// (or fails). Decrements the reference count and auto-unloads the
    /// model if no other requests or pins keep it alive.
    /// </summary>
    public void ReleaseAfterChat(Guid modelId) => processManager.Release(modelId);

    /// <summary>
    /// Lists available GGUF files at a URL (for user selection).
    /// </summary>
    public async Task<IReadOnlyList<ResolvedModelFileResponse>> ListAvailableFilesAsync(
        string url, CancellationToken ct = default)
    {
        var files = await urlResolver.ResolveAsync(url, ct);
        return files
            .Select(f => new ResolvedModelFileResponse(f.DownloadUrl, f.Filename, f.Quantization))
            .ToList();
    }

    /// <summary>
    /// Lists all downloaded local model files.
    /// </summary>
    public async Task<IReadOnlyList<LocalModelFileResponse>> ListLocalModelsAsync(
        CancellationToken ct = default)
    {
        var files = await db.LocalModelFiles
            .Include(f => f.Model)
            .Include(f => f.Model.Provider)
            .ToListAsync(ct);

        return files.Select(f => new LocalModelFileResponse(
                f.Id, f.ModelId, f.Model.Name, f.SourceUrl,
                f.FilePath, f.FileSizeBytes, f.Quantization,
                f.Status, f.DownloadProgress, processManager.IsLoaded(f.ModelId),
                f.Model.Provider.ProviderKey, f.MmprojPath))
            .ToList();
    }

    /// <summary>
    /// Persists an optional CLIP / mmproj file path for a registered LlamaSharp model.
    /// Pass <c>null</c> to clear it. The new value takes effect the next time the
    /// model is loaded; if the model is already in memory it must be unloaded first.
    /// </summary>
    public async Task SetMmprojPathAsync(Guid modelId, string? mmprojPath, CancellationToken ct = default)
    {
        var localFile = await db.LocalModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == modelId, ct)
            ?? throw new ArgumentException("No local file found for this model.");

        localFile.MmprojPath = mmprojPath;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a downloaded model file from disk and removes its records.
    /// </summary>
    public async Task<bool> DeleteLocalModelAsync(Guid modelId, CancellationToken ct = default)
    {
        var localFile = await db.LocalModelFiles
            .FirstOrDefaultAsync(f => f.ModelId == modelId, ct);

        if (localFile is null) return false;

        // Unload from memory if loaded
        processManager.Unload(modelId);

        // Only delete the file when no other provider row still references this path
        var sharedCount = await db.LocalModelFiles
            .CountAsync(f => f.FilePath == localFile.FilePath && f.ModelId != modelId, ct);

        if (sharedCount == 0 && File.Exists(localFile.FilePath))
        {
            PathGuard.EnsureContainedIn(localFile.FilePath, ModelDownloadManager.ModelsDirectoryPath);
            File.Delete(localFile.FilePath);
        }

        // Remove the local file record (model record cascade-deleted via provider)
        db.LocalModelFiles.Remove(localFile);

        // Remove the model record itself
        var model = await db.Models.FindAsync([modelId], ct);
        if (model is not null)
            db.Models.Remove(model);

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ProviderDB> EnsureLocalProviderAsync(CancellationToken ct)
    {
        var existing = await db.Providers
            .FirstOrDefaultAsync(p => p.ProviderKey == WellKnownProviderKeys.LlamaSharp, ct);

        if (existing is not null) return existing;

        // L-012: LlamaSharp runs in-process via LocalInferenceApiClient.
        // No HTTP endpoint is involved, so ApiEndpoint is intentionally left null
        // to avoid misleading operators/tooling that surface the URL.
        var provider = new ProviderDB
        {
            Name = "LlamaSharp (Local)",
            ProviderKey = WellKnownProviderKeys.LlamaSharp
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }

    }

