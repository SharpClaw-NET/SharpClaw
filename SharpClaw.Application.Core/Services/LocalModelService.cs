using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Contracts.DTOs.LocalModels;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Enums;
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
    /// <paramref name="request"/>.<c>ProviderType</c> must be non-null;
    /// call <see cref="DownloadAndRegisterBothAsync"/> when it is omitted.
    /// </summary>
    public async Task<ModelResponse> DownloadAndRegisterAsync(
        DownloadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request.ProviderType,
            $"{nameof(request)}.{nameof(request.ProviderType)}");

        var (provider, defaultCapability) = request.ProviderType switch
        {
            ProviderType.LlamaSharp => (await EnsureLocalProviderAsync(ct),   ModelCapability.Chat),
            ProviderType.Whisper    => (await EnsureWhisperProviderAsync(ct), ModelCapability.Transcription),
            _ => throw new ArgumentException(
                $"Provider type '{request.ProviderType}' does not support local file download. " +
                "Only LlamaSharp and Whisper are supported.", nameof(request))
        };

        return await DownloadAndRegisterCoreAsync(request, provider, defaultCapability, progress, ct);
    }

    /// <summary>
    /// Downloads a model once and registers it under all compatible local providers
    /// (LlamaSharp + Whisper). The GGUF architecture header is probed after download
    /// to skip providers that are clearly incompatible; a null member in the response
    /// means that provider was skipped, not that an error occurred.
    /// </summary>
    public async Task<BothModelsResponse> DownloadAndRegisterBothAsync(
        DownloadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var downloaded      = await DownloadFileAsync(request, progress, ct);
        var modelName       = request.Name ?? Path.GetFileNameWithoutExtension(downloaded.Target.Filename);
        var architecture    = await GgufHeaderReader.ReadArchitectureAsync(downloaded.DestPath, ct);
        var targets         = GgufArchitectureClassifier.Classify(architecture);

        var llamaProvider   = targets.LlamaSharp ? await EnsureLocalProviderAsync(ct)   : null;
        var whisperProvider = targets.Whisper    ? await EnsureWhisperProviderAsync(ct) : null;

        var llamaResponse   = llamaProvider   is not null
            ? await RegisterModelAsync(downloaded, modelName, llamaProvider,   ModelCapability.Chat,          ct)
            : null;
        var whisperResponse = whisperProvider is not null
            ? await RegisterModelAsync(downloaded, modelName, whisperProvider, ModelCapability.Transcription, ct)
            : null;

        return new BothModelsResponse(llamaResponse, whisperResponse);
    }

    private async Task<ModelResponse> DownloadAndRegisterCoreAsync(
        DownloadModelRequest request,
        ProviderDB provider,
        ModelCapability defaultCapability,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var downloaded = await DownloadFileAsync(request, progress, ct);
        var modelName  = request.Name ?? Path.GetFileNameWithoutExtension(downloaded.Target.Filename);
        return await RegisterModelAsync(downloaded, modelName, provider, defaultCapability, ct);
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
        ModelCapability defaultCapability,
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
                provider.Id, provider.Name, existingModel.Capabilities);
        }

        var capabilities = ProviderService.InferCapabilities(modelName);
        if (capabilities == ModelCapability.None)
            capabilities = defaultCapability;

        var model = new ModelDB { Name = modelName, ProviderId = provider.Id, Capabilities = capabilities };
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
        return new ModelResponse(model.Id, model.Name, provider.Id, provider.Name, model.Capabilities);
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
                f.Model.Provider.ProviderType, f.MmprojPath))
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
            .FirstOrDefaultAsync(p => p.ProviderType == ProviderType.LlamaSharp, ct);

        if (existing is not null) return existing;

        // L-012: LlamaSharp runs in-process via LocalInferenceApiClient.
        // No HTTP endpoint is involved, so ApiEndpoint is intentionally left null
        // to avoid misleading operators/tooling that surface the URL.
        var provider = new ProviderDB
        {
            Name = "LlamaSharp (Local)",
            ProviderType = ProviderType.LlamaSharp
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }

    private async Task<ProviderDB> EnsureWhisperProviderAsync(CancellationToken ct)
    {
        var existing = await db.Providers
            .FirstOrDefaultAsync(p => p.ProviderType == ProviderType.Whisper, ct);

        if (existing is not null) return existing;

        var provider = new ProviderDB
        {
            Name = "Whisper (Local)",
            ProviderType = ProviderType.Whisper,
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }
}
