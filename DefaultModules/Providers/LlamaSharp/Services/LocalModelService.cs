using SharpClaw.Modules.Providers.LlamaSharp.LocalInference;
using SharpClaw.Providers.Common;
using SharpClaw.Providers.LocalCommon;
using SharpClaw.Modules.Providers.LlamaSharp.LocalModels;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.Providers.LlamaSharp.Services;

public sealed class LocalModelService(
    LocalModelStore store,
    HuggingFaceUrlResolver urlResolver,
    ModelDownloadManager downloadManager,
    LocalInferenceProcessManager processManager,
    IModelRegistrar registrar)
{
    /// <summary>
    /// Downloads a model and registers it under an explicit provider.
    /// <paramref name="request"/>.<c>ProviderKey</c> must be non-null.
    /// </summary>
    public async Task<ModelResponse> DownloadAndRegisterAsync(
        DownloadModelRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request.ProviderKey,
            $"{nameof(request)}.{nameof(request.ProviderKey)}");

        if (request.ProviderKey != "llamasharp")
            throw new ArgumentException(
                $"Provider key '{request.ProviderKey}' does not support local file download. " +
                "Only LlamaSharp is supported by this module.",
                nameof(request));

        var providerId = await EnsureLocalProviderAsync(ct);

        return await DownloadAndRegisterCoreAsync(
            request, providerId, WellKnownCapabilityKeys.Chat, progress, ct);
    }

    private async Task<ModelResponse> DownloadAndRegisterCoreAsync(
        DownloadModelRequest request,
        Guid providerId,
        string defaultCapability,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var target = await ResolveDownloadTargetAsync(request, ct);
        var modelName = request.Name ?? Path.GetFileNameWithoutExtension(target.Target.Filename);

        var (modelId, fileId) = await CreateOrReuseDownloadPlaceholderAsync(
            modelName, providerId, defaultCapability, target, ct);

        var dbProgress = new ThrottledDbProgressWriter(store, fileId, progress);
        try
        {
            await downloadManager.DownloadAsync(target.DownloadUrl, target.DestPath, dbProgress, ct);
        }
        catch
        {
            await MarkDownloadFailedAsync(fileId, ct);
            throw;
        }

        return await FinaliseDownloadAsync(modelId, fileId, target, ct);
    }

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

    private async Task<(Guid ModelId, Guid FileId)> CreateOrReuseDownloadPlaceholderAsync(
        string modelName,
        Guid providerId,
        string defaultCapability,
        ResolvedDownloadTarget target,
        CancellationToken ct)
    {
        var tags = ProviderCapabilityHeuristics.ForGeneric(modelName).ToList();
        if (tags.Count == 0)
            tags = [defaultCapability];

        var modelId = await registrar.EnsureModelAsync(modelName, providerId, tags, ct);

        return await store.CreateOrReuseDownloadPlaceholderAsync(
            modelId,
            target.Target,
            target.RequestUrl,
            target.DestPath,
            ct);
    }

    private async Task MarkDownloadFailedAsync(Guid fileId, CancellationToken ct)
    {
        await store.MarkDownloadFailedAsync(fileId, ct);
    }

    private async Task<ModelResponse> FinaliseDownloadAsync(
        Guid modelId, Guid fileId, ResolvedDownloadTarget target, CancellationToken ct)
    {
        await store.FinaliseDownloadAsync(
            fileId,
            target.Target,
            target.DestPath,
            new FileInfo(target.DestPath).Length,
            ct);

        var meta = await registrar.GetModelMetadataAsync(modelId, ct)
            ?? throw new InvalidOperationException($"Model {modelId} disappeared during finalisation.");

        return new ModelResponse(modelId, meta.Name, meta.ProviderId, meta.ProviderName,
            meta.CustomId, meta.CapabilityTags);
    }

    private sealed record ResolvedDownloadTarget(
        ResolvedModelFile Target, string DownloadUrl, string DestPath, string RequestUrl);

    private sealed class ThrottledDbProgressWriter(
        LocalModelStore store,
        Guid fileId,
        IProgress<double>? inner) : IProgress<double>
    {
        private int _lastWrittenPct = -1;

        public void Report(double value)
        {
            inner?.Report(value);

            if (value < 0) value = 0;
            if (value >= 1) return;

            var pct = (int)(value * 100);
            if (pct <= _lastWrittenPct) return;
            _lastWrittenPct = pct;

            _ = UpdateAsync(value);
        }

        private async Task UpdateAsync(double value)
        {
            try
            {
                await store.UpdateDownloadProgressAsync(fileId, value);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Pins the model so it stays loaded between requests.
    /// </summary>
    public async Task LoadModelAsync(
        Guid modelId, LoadModelRequest request, CancellationToken ct = default)
    {
        var localFile = await store.GetByModelIdAsync(modelId, ct)
            ?? throw new ArgumentException("No local file found for this model.");

        if (localFile.Status != LocalModelStatus.Ready)
            throw new InvalidOperationException($"Model file status is {localFile.Status}.");

        await processManager.PinAsync(
            modelId, localFile.FilePath, request.GpuLayers, request.ContextSize,
            localFile.MmprojPath ?? request.MmprojPath, ct);
    }

    public Task UnloadModelAsync(Guid modelId, CancellationToken ct = default)
    {
        processManager.Unpin(modelId);
        return Task.CompletedTask;
    }

    // ── Chat auto-load lifecycle ──────────────────────────────────

    public async Task EnsureReadyForChatAsync(Guid modelId, CancellationToken ct)
    {
        var localFile = await store.GetByModelIdAsync(modelId, ct)
            ?? throw new InvalidOperationException("No local file found for this model.");

        if (localFile.Status != LocalModelStatus.Ready)
            throw new InvalidOperationException($"Model file status is {localFile.Status}.");

        await processManager.AcquireAsync(modelId, localFile.FilePath, mmprojPath: localFile.MmprojPath, ct: ct);
    }

    public void ReleaseAfterChat(Guid modelId) => processManager.Release(modelId);

    public async Task<IReadOnlyList<ResolvedModelFileResponse>> ListAvailableFilesAsync(
        string url, CancellationToken ct = default)
    {
        var files = await urlResolver.ResolveAsync(url, ct);
        return files
            .Select(f => new ResolvedModelFileResponse(f.DownloadUrl, f.Filename, f.Quantization))
            .ToList();
    }

    public async Task<IReadOnlyList<LocalModelFileResponse>> ListLocalModelsAsync(
        CancellationToken ct = default)
    {
        var files = await store.ListAsync(ct);

        var results = new List<LocalModelFileResponse>(files.Count);
        foreach (var f in files)
        {
            var meta = await registrar.GetModelMetadataAsync(f.ModelId, ct);
            results.Add(new LocalModelFileResponse(
                f.Id, f.ModelId,
                meta?.Name ?? "(unknown)",
                f.SourceUrl, f.FilePath, f.FileSizeBytes, f.Quantization,
                f.Status, f.DownloadProgress, processManager.IsLoaded(f.ModelId),
                meta?.ProviderKey ?? string.Empty, f.MmprojPath));
        }
        return results;
    }

    public async Task SetMmprojPathAsync(Guid modelId, string? mmprojPath, CancellationToken ct = default)
    {
        await store.SetMmprojPathAsync(modelId, mmprojPath, ct);
    }

    public async Task<bool> DeleteLocalModelAsync(Guid modelId, CancellationToken ct = default)
    {
        var localFile = await store.GetByModelIdAsync(modelId, ct);

        if (localFile is null) return false;

        processManager.Unload(modelId);

        var sharedCount = (await store.ListAsync(ct))
            .Count(f => f.FilePath == localFile.FilePath && f.ModelId != modelId);

        if (sharedCount == 0 && File.Exists(localFile.FilePath))
        {
            PathGuard.EnsureContainedIn(localFile.FilePath, ModelDownloadManager.ModelsDirectoryPath);
            File.Delete(localFile.FilePath);
        }

        await store.DeleteByModelIdAsync(modelId, ct);

        await registrar.DeleteModelAsync(modelId, ct);
        return true;
    }

    private async Task<Guid> EnsureLocalProviderAsync(CancellationToken ct) =>
        await registrar.EnsureProviderAsync(
            "llamasharp", "LlamaSharp (Local)", ct);
}
