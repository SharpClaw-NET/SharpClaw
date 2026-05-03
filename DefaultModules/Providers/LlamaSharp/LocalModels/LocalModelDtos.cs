namespace SharpClaw.Modules.Providers.LlamaSharp.LocalModels;

public sealed record DownloadModelRequest(
    string Url,
    string? Name = null,
    string? Quantization = null,
    int? GpuLayers = null,
    string? ProviderKey = null);

public sealed record LoadModelRequest(
    int? GpuLayers = null,
    uint? ContextSize = null,
    string? MmprojPath = null);

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
    string ProviderKey,
    string? MmprojPath);

/// <summary>
/// Sets or clears the CLIP / mmproj file path for a registered LlamaSharp model.
/// Pass <c>null</c> to clear it.
/// </summary>
public sealed record SetMmprojRequest(string? MmprojPath);
