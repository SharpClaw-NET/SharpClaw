using System.Text.Json.Serialization;

namespace SharpClaw.Utils.Instances;

public sealed class SharpClawDiscoveryEntry
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SharpClawInstanceKind InstanceKind { get; set; }

    public required string InstanceId { get; set; }

    public required string InstallFingerprint { get; set; }

    public required string InstanceRoot { get; set; }

    public required string BaseUrl { get; set; }

    public required string RuntimeDirectory { get; set; }

    public required string ApiKeyFilePath { get; set; }

    public string? GatewayTokenFilePath { get; set; }

    public int ProcessId { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}
