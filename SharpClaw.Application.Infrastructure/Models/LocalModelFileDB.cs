using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Infrastructure.Models;

public class LocalModelFileDB : BaseEntity
{
    public Guid ModelId { get; set; }
    public ModelDB Model { get; set; } = null!;

    /// <summary>
    /// Original download URL (HuggingFace, direct GGUF link, etc.).
    /// </summary>
    public required string SourceUrl { get; set; }

    /// <summary>
    /// Absolute path to the downloaded file on disk.
    /// </summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// SHA-256 hash of the file for integrity verification.
    /// </summary>
    public string? Sha256Hash { get; set; }

    /// <summary>
    /// Quantization level parsed from filename (e.g. Q4_K_M, Q5_K_S).
    /// </summary>
    public string? Quantization { get; set; }

    public LocalModelStatus Status { get; set; } = LocalModelStatus.Pending;

    /// <summary>
    /// Download progress (0.0 – 1.0).
    /// </summary>
    public double DownloadProgress { get; set; }

    /// <summary>
    /// Port the local inference server is currently bound to (null if not running).
    /// </summary>
    public int? ActivePort { get; set; }

    /// <summary>
    /// Optional absolute path to the CLIP / mmproj model file required for
    /// multimodal (LLaVA-style) inference. When set, the CLIP projector is
    /// loaded alongside the main GGUF weights and image inputs can be embedded.
    /// Null means the model is text-only.
    /// </summary>
    public string? MmprojPath { get; set; }
}
