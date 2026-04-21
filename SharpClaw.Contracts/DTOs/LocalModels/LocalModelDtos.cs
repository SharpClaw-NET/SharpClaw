using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.LocalModels;

/// <summary>
/// Returned by the dual-registration download path (no explicit <c>providerType</c>).
/// A null member means that provider was skipped by the architecture probe, not that
/// an error occurred.
/// </summary>
public sealed record BothModelsResponse(ModelResponse? LlamaSharp, ModelResponse? Whisper);

public sealed record DownloadModelRequest(
    string Url,
    string? Name = null,
    string? Quantization = null,
    int? GpuLayers = null,
    ProviderType? ProviderType = null);

public sealed record LoadModelRequest(
    int? GpuLayers = null,
    uint? ContextSize = null);

public sealed record ResolvedModelFileResponse(
    string DownloadUrl,
    string Filename,
    string? Quantization);

public sealed record LocalModelFileResponse(
    Guid Id,
    Guid ModelId,
    string ModelName,
    string SourceUrl,
    string FilePath,
    long FileSizeBytes,
    string? Quantization,
    LocalModelStatus Status,
    double DownloadProgress,
    bool IsLoaded,
    ProviderType ProviderType);
