using System.Text.Json.Serialization;

namespace SharpClaw.Shared.Instances;

public sealed class SharpClawDiscoveryEntry
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
    public required string BaseUrl { get; set; }

    public required string RuntimeDirectory { get; set; }

    public required string ApiKeyFilePath { get; set; }

    public string? GatewayTokenFilePath { get; set; }

    public int ProcessId { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}
