using System.Text.Json;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Shared secure JSON options for all module-related deserialization.
/// </summary>
internal static class SecureJsonOptions
{
    public static readonly JsonSerializerOptions Envelope = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Manifest = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions ManifestWrite = new()
    {
        WriteIndented = true,
    };

    /// <summary>Max ScriptJson size in bytes before parsing is rejected.</summary>
    public const int MaxEnvelopeSize = 1 * 1024 * 1024; // 1 MB
}
