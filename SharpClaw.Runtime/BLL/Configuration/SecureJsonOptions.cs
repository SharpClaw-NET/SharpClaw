using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Configuration;

/// <summary>
/// Shared secure JSON options for all registration-related deserialization.
/// </summary>
internal static class SecureJsonOptions
{
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

    /// <summary>
    /// Default max ScriptJson size in bytes before parsing is rejected.
    /// Operators may override this at runtime through the
    /// <c>Packages:MaxEnvelopeSizeBytes</c> configuration key (see
    /// <see cref="GetMaxEnvelopeSize"/>); the constant is kept as a safe
    /// fallback when no configuration is wired (test scenarios) and as a
    /// stable default for new deployments.
    /// </summary>
    public const int DefaultMaxEnvelopeSize = 1 * 1024 * 1024; // 1 MB

    /// <summary>Configuration key for overriding <see cref="DefaultMaxEnvelopeSize"/>.</summary>
    public const string MaxEnvelopeSizeConfigKey = "Packages:MaxEnvelopeSizeBytes";

    public static PackageManifest DeserializeManifest(string json)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
            });

        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(property =>
                string.Equals(property.Name, "entrypoint", StringComparison.OrdinalIgnoreCase)))
        {
            throw new JsonException(
                "The registration manifest property 'entrypoint' was removed. Use 'entryAssembly' for .NET registrations.");
        }

        return JsonSerializer.Deserialize<PackageManifest>(json, Manifest)
            ?? throw new JsonException("Registration manifest must contain a JSON object.");
    }

    /// <summary>
    /// Resolve the active envelope size cap from configuration, clamped
    /// to a positive value. Falls back to <see cref="DefaultMaxEnvelopeSize"/>
    /// when <paramref name="configuration"/> is <c>null</c> or the key is
    /// unset / non-positive.
    /// </summary>
    public static int GetMaxEnvelopeSize(IConfiguration? configuration)
    {
        if (configuration is null)
            return DefaultMaxEnvelopeSize;

        var configured = configuration.GetValue(MaxEnvelopeSizeConfigKey, DefaultMaxEnvelopeSize);
        return configured > 0 ? configured : DefaultMaxEnvelopeSize;
    }
}
