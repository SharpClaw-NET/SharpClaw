namespace SharpClaw.Modules.Http;

/// <summary>
/// Trigger and parameter keys owned by the HTTP module for the network
/// trigger family (host reachability and availability changes).
/// String values are persisted verbatim in binding rows and serialized
/// scripts.
/// </summary>
public static class NetworkTriggerKeys
{
    public const string HostReachable   = "HostReachable";
    public const string HostUnreachable = "HostUnreachable";
    public const string NetworkChanged  = "NetworkChanged";

    // Parameter names — must match TaskTriggerDefinition property names.
    public const string HostName     = "HostName";
    public const string HostPort     = "HostPort";
    public const string NetworkSsid  = "NetworkSsid";
    public const string NetworkState = "NetworkState";
}
