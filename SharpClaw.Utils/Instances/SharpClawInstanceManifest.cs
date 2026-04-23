using System.Text.Json.Serialization;

namespace SharpClaw.Utils.Instances;

public sealed class SharpClawInstanceManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SharpClawInstanceKind InstanceKind { get; set; }

    public required string InstanceId { get; set; }

    public required string InstallFingerprint { get; set; }

    public required string InstanceRoot { get; set; }

    public string? BaseUrl { get; set; }

    public string? DataDirectory { get; set; }

    public string? SelectedBackendInstanceId { get; set; }

    public string? SelectedBackendBaseUrl { get; set; }

    public string? SelectedBackendBindingKind { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LegacyImportCompletedAtUtc { get; set; }
}
