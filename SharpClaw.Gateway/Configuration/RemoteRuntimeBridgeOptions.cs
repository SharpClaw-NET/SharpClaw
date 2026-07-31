using Microsoft.Extensions.Configuration;

namespace SharpClaw.Gateway.Configuration;

public sealed class RemoteRuntimeBridgeOptions
{
    public const string SectionName = "Gateway:RemoteRuntimeBridge";

    public bool Enabled { get; init; }

    public string ListenUrl { get; init; } = "https://127.0.0.1:48925";

    public string? PairingFile { get; init; }

    public static RemoteRuntimeBridgeOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new RemoteRuntimeBridgeOptions
        {
            Enabled = section.GetValue("Enabled", false),
            ListenUrl = section.GetValue("ListenUrl", "https://127.0.0.1:48925"),
            PairingFile = section["PairingFile"],
        };
    }
}
