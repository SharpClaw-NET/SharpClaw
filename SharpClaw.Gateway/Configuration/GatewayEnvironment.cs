using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Loads gateway environment configuration from <c>Environment/.env</c>
/// (always) and <c>Environment/.dev.env</c> (development only) relative
/// to the assembly location. Creates a default <c>.env</c> if one does
/// not exist.
/// </summary>
public static class GatewayEnvironment
{
    private const string DefaultEnvContent =
        """
        {
          // SharpClaw Gateway Environment Configuration
          // Values here are loaded for all environments.

          "InternalApi": {
            "BaseUrl": "http://127.0.0.1:48923",
            "TimeoutSeconds": "300",
            "ApiKey": "",
            "ApiKeyFilePath": "",
            "GatewayToken": "",
            "GatewayTokenFilePath": ""
          },

          "Logging": {
            "Serilog": {
              "Enabled": "true",
              "ConsoleEnabled": "true",
              "FileEnabled": "true",
              "RequestLoggingEnabled": "true",
              "MinimumLevel": "Information",
              "MicrosoftMinimumLevel": "Warning",
              "AspNetCoreMinimumLevel": "Warning",
              "EntityFrameworkCoreMinimumLevel": "Warning",
              "UnoMinimumLevel": "Warning"
            }
          },

          "Gateway": {
            "RequestQueue": {
              "Enabled": "true",
              "MaxConcurrency": "1",
              "TimeoutSeconds": "30",
              "MaxRetries": "2",
              "RetryDelayMs": "500",
              "MaxQueueSize": "500"
            },

            "Endpoints": {
              "Enabled": "true",
              "Auth": "false",
              "Agents": "false",
              "Channels": "false",
              "ChannelContexts": "false",
              "Chat": "false",
              "ChatStream": "false",
              "Threads": "false",
              "ThreadChat": "false",
              "ThreadWatch": "false",
              "Jobs": "false",
              "Models": "false",
              "LocalModels": "false",
              "Providers": "false",
              "Roles": "false",
              "Users": "false",
              "Cost": "false",
              "Tasks": "false",
              "TaskStreaming": "false",
              "ToolAwarenessSets": "false",
              "Resources": "false"
            },

            "Modules": {
              "Modules": {},
              "Groups": {},
              "HotReloadEnabled": "false",
              "DrainTimeoutSeconds": "30"
            }
          }
        }
        """;

    public static IConfigurationBuilder AddGatewayEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(GatewayEnvironment).Assembly.Location)!,
            "Environment");

        EnsureEnvironmentFile(envDir);

        if (!Directory.Exists(envDir))
            return builder;

        // PhysicalFileProvider defaults to ExclusionFilters.Sensitive which
        // excludes dot-prefixed files (.env, .dev.env). Use None so the
        // configuration system can see them.
        var fileProvider = new PhysicalFileProvider(envDir, ExclusionFilters.None);

        builder.AddJsonFile(fileProvider, ".env", optional: true, reloadOnChange: false);

        if (isDevelopment)
            builder.AddJsonFile(fileProvider, ".dev.env", optional: true, reloadOnChange: false);

        return builder;
    }

    private static void EnsureEnvironmentFile(string envDir)
    {
        var envFile = Path.Combine(envDir, ".env");
        if (File.Exists(envFile) && new FileInfo(envFile).Length > 0)
            return;

        try
        {
            Directory.CreateDirectory(envDir);
            File.WriteAllText(envFile, DefaultEnvContent);
        }
        catch
        {
            // Best-effort; read-only or restricted file system.
        }
    }
}
