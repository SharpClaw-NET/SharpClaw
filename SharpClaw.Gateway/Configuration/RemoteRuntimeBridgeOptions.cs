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
