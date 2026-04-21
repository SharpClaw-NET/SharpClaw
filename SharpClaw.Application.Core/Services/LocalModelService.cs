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
    /// Downloads a model from a URL (HuggingFace or direct) and registers
    /// it as a Local provider + model in the database.
    /// </summary>
    public async Task<ModelResponse> DownloadAndRegisterAsync(
        DownloadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var files = await urlResolver.ResolveAsync(request.Url, ct);
        if (files.Count == 0)
            throw new ArgumentException("No downloadable GGUF files found at the URL.");

        // Pick matching quantization or first file
        var target = request.Quantization is not null
            ? files.FirstOrDefault(f =>
                f.Quantization?.Equals(request.Quantization, StringComparison.OrdinalIgnoreCase) == true)
              ?? files[0]
            : files[0];

        var sourceFolder = ModelDownloadManager.ResolveSourceFolder(request.Url);
        var destPath = downloadManager.GetModelPath(sourceFolder, target.Filename);

        await downloadManager.DownloadAsync(target.DownloadUrl, destPath, progress, ct);

        var localProvider = await EnsureLocalProviderAsync(ct);

        var modelName = request.Name ?? Path.GetFileNameWithoutExtension(target.Filename);

        // Avoid duplicate model names under the Local provider
        var existingModel = await db.Models
            .FirstOrDefaultAsync(m => m.Name == modelName && m.ProviderId == localProvider.Id, ct);

        if (existingModel is not null)
        {
            // Update the local file record if it already exists
            var existingFile = await db.LocalModelFiles
                .FirstOrDefaultAsync(f => f.ModelId == existingModel.Id, ct);

            if (existingFile is not null)
            {
                existingFile.FilePath = destPath;
                existingFile.FileSizeBytes = new FileInfo(destPath).Length;
                existingFile.Status = LocalModelStatus.Ready;
                existingFile.DownloadProgress = 1.0;
                existingFile.Quantization = target.Quantization;
                await db.SaveChangesAsync(ct);
            }

            return new ModelResponse(existingModel.Id, existingModel.Name,
                localProvider.Id, "Local", existingModel.Capabilities);
        }

        var capabilities = ProviderService.InferCapabilities(modelName);
        if (capabilities == ModelCapability.None)
            capabilities = ModelCapability.Chat;

        var model = new ModelDB
        {
            Name = modelName,
            ProviderId = localProvider.Id,
            Capabilities = capabilities
        };
        db.Models.Add(model);

        var fileInfo = new FileInfo(destPath);
        db.LocalModelFiles.Add(new LocalModelFileDB
        {
            ModelId = model.Id,
            SourceUrl = request.Url,
            FilePath = destPath,
            FileSizeBytes = fileInfo.Length,
            Quantization = target.Quantization,
            Status = LocalModelStatus.Ready,
            DownloadProgress = 1.0
        });

        await db.SaveChangesAsync(ct);
        return new ModelResponse(model.Id, model.Name, localProvider.Id, "Local", model.Capabilities);
    }

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
            modelId, localFile.FilePath, request.GpuLayers, request.ContextSize, ct);
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

        await processManager.AcquireAsync(modelId, localFile.FilePath, ct: ct);
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
            .ToListAsync(ct);

        return files.Select(f => new LocalModelFileResponse(
                f.Id, f.ModelId, f.Model.Name, f.SourceUrl,
                f.FilePath, f.FileSizeBytes, f.Quantization,
                f.Status, f.DownloadProgress, processManager.IsLoaded(f.ModelId)))
            .ToList();
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

        // Delete from disk (validate path stays inside the models directory)
        if (File.Exists(localFile.FilePath))
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

        var provider = new ProviderDB
        {
            Name = "LlamaSharp (Local)",
            ProviderType = ProviderType.LlamaSharp,
            ApiEndpoint = "http://localhost:18080/v1"
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }
}
