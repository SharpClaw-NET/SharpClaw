using Microsoft.Extensions.Configuration;

namespace SharpClaw.Application.Core.Modules;

internal enum DotNetModuleHostingMode
{
    SidecarOnly
}

internal static class DotNetModuleHostingModeOptions
{
    public const string ConfigKey = "Modules:DotNetHostingMode";

    public static DotNetModuleHostingMode Resolve(IConfiguration? configuration) =>
        Parse(configuration?[ConfigKey]);

    public static DotNetModuleHostingMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DotNetModuleHostingMode.SidecarOnly;

        return value.Trim().ToLowerInvariant() switch
        {
            "default" or "auto" or "sidecar-only" or "sidecaronly" or "sidecar_only" =>
                DotNetModuleHostingMode.SidecarOnly,
            _ => throw new InvalidOperationException(
                $"Unsupported {ConfigKey} value '{value}'. Allowed values are sidecar-only, default, and auto.")
        };
    }
}
