namespace SharpClaw.Application.Core.Modules.Foreign;

public static class ForeignModuleProtocol
{
    public const int Version = 1;
    public const string TokenHeaderName = "X-SharpClaw-Control-Token";

    public const string ModuleDirectoryEnv = "SHARPCLAW_MODULE_DIR";
    public const string ModuleDataDirectoryEnv = "SHARPCLAW_MODULE_DATA_DIR";
    public const string ControlAddressEnv = "SHARPCLAW_CONTROL_ADDRESS";
    public const string ControlTokenEnv = "SHARPCLAW_CONTROL_TOKEN";
    public const string ModuleIdEnv = "SHARPCLAW_MODULE_ID";
    public const string ModuleRuntimeEnv = "SHARPCLAW_MODULE_RUNTIME";

    public const string HandshakePath = "/.sharpclaw/handshake";
    public const string HealthPath = "/.sharpclaw/health";
    public const string InitializePath = "/.sharpclaw/initialize";
    public const string ShutdownPath = "/.sharpclaw/shutdown";
    public const string DiscoveryPath = "/.sharpclaw/discovery";
}

internal static class ForeignModuleCapability
{
    public const string Endpoints = "endpoints";
    public const string JobTools = "jobTools";
    public const string InlineTools = "inlineTools";
    public const string StreamingTools = "streamingTools";
    public const string FrontendContributions = "frontendContributions";
    public const string LifecycleHooks = "lifecycleHooks";
    public const string HostCapabilities = "hostCapabilities";
}

public static class ForeignModuleEndpointResponseMode
{
    public const string Json = "json";
    public const string Stream = "stream";
    public const string Static = "static";
    public const string Raw = "raw";
}
