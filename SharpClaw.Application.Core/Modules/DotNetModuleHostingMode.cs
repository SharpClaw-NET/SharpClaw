using Microsoft.Extensions.Configuration;

namespace SharpClaw.Application.Core.Modules;

internal enum DotNetModuleHostingMode
{
    SidecarOnly,
    AllowInProcess,
    InProcess
}

internal static class DotNetModuleHostingModeOptions
{
    public const string ConfigKey = "Modules:DotNetHostingMode";
    public const string EnvironmentKey = "SHARPCLAW_DOTNET_MODULE_HOSTING";

    public static DotNetModuleHostingMode Resolve(IConfiguration? configuration)
    {
        var environmentValue = Environment.GetEnvironmentVariable(EnvironmentKey);
        return Parse(string.IsNullOrWhiteSpace(environmentValue)
            ? configuration?[ConfigKey]
            : environmentValue);
    }

    public static DotNetModuleHostingMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DotNetModuleHostingMode.SidecarOnly;

        return value.Trim().ToLowerInvariant() switch
        {
            "default" or "auto" or "sidecar-only" or "sidecaronly" or "sidecar_only" =>
                DotNetModuleHostingMode.SidecarOnly,
            "allow-in-process" or "allowinprocess" or "allow_in_process" =>
                DotNetModuleHostingMode.AllowInProcess,
            "in-process" or "inprocess" or "in_process" =>
                DotNetModuleHostingMode.InProcess,
            _ => throw new InvalidOperationException(
                $"Unsupported {ConfigKey} value '{value}'. Allowed values are sidecar-only, allow-in-process, in-process, default, and auto.")
        };
    }
}
