using System.Text.Json.Serialization;

namespace SharpClaw.Shared.Instances;

public sealed class SharpClawInstanceManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SharpClawInstanceKind InstanceKind { get; set; }

    public required string InstanceId { get; set; }

    public required string InstallFingerprint { get; set; }

    public required string InstanceRoot { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056",
        Justification = "This published string property preserves the versioned JSON manifest/discovery representation and existing CLR getter/setter ABI. Persisted endpoint text is retained exactly; converting its public type to Uri would break those contracts.")]
    public string? BaseUrl { get; set; }

    public string? DataDirectory { get; set; }

    public string? SelectedBackendInstanceId { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056",
        Justification = "This published string property preserves the versioned JSON manifest/discovery representation and existing CLR getter/setter ABI. Persisted endpoint text is retained exactly; converting its public type to Uri would break those contracts.")]
    public string? SelectedBackendBaseUrl { get; set; }

    public string? SelectedBackendBindingKind { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LegacyImportCompletedAtUtc { get; set; }
}
