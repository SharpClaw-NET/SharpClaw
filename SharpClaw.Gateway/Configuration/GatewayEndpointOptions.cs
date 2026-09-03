namespace SharpClaw.Gateway.Configuration;

public sealed class GatewayEndpointOptions
{
    public const string SectionName = "Gateway:Endpoints";

    public bool Enabled { get; set; } = true;
}
