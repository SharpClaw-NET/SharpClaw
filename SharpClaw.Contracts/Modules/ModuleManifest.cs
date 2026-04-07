using System.Text.Json.Serialization;

namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Strongly-typed representation of a module's <c>module.json</c> manifest.
/// Deserialized with hardened <c>JsonSerializerOptions</c> (MaxDepth=8).
/// </summary>
public sealed record ModuleManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("toolPrefix")] string ToolPrefix,
    [property: JsonPropertyName("entryAssembly")] string EntryAssembly,
    [property: JsonPropertyName("minHostVersion")] string MinHostVersion,
    [property: JsonPropertyName("author")] string? Author = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("license")] string? License = null,
    [property: JsonPropertyName("platforms")] string[]? Platforms = null,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("defaultEnabled")] bool DefaultEnabled = true,
    [property: JsonPropertyName("executionTimeoutSeconds")] int ExecutionTimeoutSeconds = 60,
    [property: JsonPropertyName("exports")] string[]? Exports = null,
    [property: JsonPropertyName("requires")] string[]? Requires = null
);
