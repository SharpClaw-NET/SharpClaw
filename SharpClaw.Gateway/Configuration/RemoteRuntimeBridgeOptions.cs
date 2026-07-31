using Microsoft.Extensions.Configuration;

namespace SharpClaw.Gateway.Configuration;

public sealed class RemoteRuntimeBridgeOptions
{
    public const string SectionName = "Gateway:RemoteRuntimeBridge";

    public bool Enabled { get; init; }

    public string ListenUrl { get; init; } = "https://127.0.0.1:48925";

    public string? ServerCertificatePath { get; init; }

    public string? AdministrationKey { get; init; }

    public int MaxConcurrentRequestsPerPair { get; init; } = 64;

    public int MaxConcurrentStreamsPerPair { get; init; } = 8;

    public int MaxConcurrentWebSocketsPerPair { get; init; } = 8;

    public int MaxConcurrentRequests { get; init; } = 256;

    public int MaxConcurrentStreams { get; init; } = 32;

    public int MaxConcurrentWebSockets { get; init; } = 32;

    public int MaxConcurrentPairingControls { get; init; } = 16;

    public int LastSeenUpdateIntervalSeconds { get; init; } = 30;

    public static RemoteRuntimeBridgeOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", false);
        if (!enabled)
        {
            return new RemoteRuntimeBridgeOptions
            {
                Enabled = false,
                ListenUrl = section.GetValue("ListenUrl", "https://127.0.0.1:48925"),
            };
        }

        return new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = section.GetValue("ListenUrl", "https://127.0.0.1:48925"),
            ServerCertificatePath = section["ServerCertificatePath"],
            AdministrationKey = section["AdministrationKey"],
            MaxConcurrentRequestsPerPair = ReadBoundedInteger(
                section,
                "MaxConcurrentRequestsPerPair",
                64,
                1,
                4096),
            MaxConcurrentStreamsPerPair = ReadBoundedInteger(
                section,
                "MaxConcurrentStreamsPerPair",
                8,
                1,
                256),
            MaxConcurrentWebSocketsPerPair = ReadBoundedInteger(
                section,
                "MaxConcurrentWebSocketsPerPair",
                8,
                1,
                256),
            MaxConcurrentRequests = ReadBoundedInteger(
                section,
                "MaxConcurrentRequests",
                256,
                1,
                4096),
            MaxConcurrentStreams = ReadBoundedInteger(
                section,
                "MaxConcurrentStreams",
                32,
                1,
                256),
            MaxConcurrentWebSockets = ReadBoundedInteger(
                section,
                "MaxConcurrentWebSockets",
                32,
                1,
                256),
            MaxConcurrentPairingControls = ReadBoundedInteger(
                section,
                "MaxConcurrentPairingControls",
                16,
                1,
                256),
            LastSeenUpdateIntervalSeconds = ReadBoundedInteger(
                section,
                "LastSeenUpdateIntervalSeconds",
                30,
                1,
                3600),
        };
    }

    private static int ReadBoundedInteger(
        IConfigurationSection section,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = section[name];
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (!int.TryParse(value, out var result)
            || result < minimum
            || result > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }
}
