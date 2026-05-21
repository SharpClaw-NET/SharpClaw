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
    public const string ToolExecutePath = "/.sharpclaw/tools/execute";
    public const string ToolStreamPath = "/.sharpclaw/tools/stream";
    public const string InlineToolExecutePath = "/.sharpclaw/inline-tools/execute";
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

public static class ForeignModuleHostCapabilityProtocol
{
    public const string AddressEnv = "SHARPCLAW_HOST_CAPABILITIES_ADDRESS";
    public const string TokenEnv = "SHARPCLAW_HOST_CAPABILITIES_TOKEN";

    public const string ConfigGetPath = "/.sharpclaw/host/config/get";
    public const string ConfigSetPath = "/.sharpclaw/host/config/set";
    public const string ConfigAllPath = "/.sharpclaw/host/config/all";
    public const string LogPath = "/.sharpclaw/host/log";
    public const string JobLogPath = "/.sharpclaw/host/job/log";
    public const string JobCompletePath = "/.sharpclaw/host/job/complete";
    public const string JobFailPath = "/.sharpclaw/host/job/fail";
    public const string JobCancelPath = "/.sharpclaw/host/job/cancel";
}
