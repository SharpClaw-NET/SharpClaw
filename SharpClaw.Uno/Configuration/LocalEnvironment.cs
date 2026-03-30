using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace SharpClaw.Configuration;

/// <summary>
/// Loads environment configuration from <c>Environment/.env</c> (always)
/// and <c>Environment/.dev.env</c> (development only) relative to the assembly location.
/// Creates a default <c>.env</c> if one does not exist.
/// </summary>
public static class LocalEnvironment
{
    public const string DefaultApiUrl = "http://127.0.0.1:48923";
    public const string DefaultGatewayUrl = "http://0.0.0.0:48924";

    private const string DefaultEnvContent =
        """
        {
          // SharpClaw Client Environment Configuration
          // Values here are loaded for all environments.

          // -- API Server -----------------------------------------------
          // Base URL for the SharpClaw API service.
          // Change this to a remote address (e.g. "http://192.168.1.50:48923")
          // when connecting to an API instance running on another machine.
          "Api": { "Url": "http://127.0.0.1:48923" },

          // -- Backend Process -------------------------------------------
          // Set Enabled to false to prevent the client from launching a
          // bundled backend process. Use this when the API is installed
          // separately, runs as a system service, or is on another host.
          //"Backend": { "Enabled": "false" },

          // -- Public Gateway -----------------------------------------------
          // Set Enabled to false to prevent the client from launching the
          // gateway proxy. Url controls the address/port the gateway binds to.
          //"Gateway": { "Enabled": "false", "Url": "http://0.0.0.0:48924" },

          // -- Process Lifecycle --------------------------------------------
          // Persistent: keep backend/gateway running when the frontend exits.
          // AutoStart:  register them to launch at Windows login (startup folder).
          //"Processes": { "Persistent": "true", "AutoStart": "true" }
        }
        """;

    /// <summary>
    /// Reads <c>Api:Url</c> from the environment files, falling back to
    /// <see cref="DefaultApiUrl"/> when not configured.
    /// </summary>
    public static string LoadApiUrl(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        return config["Api:Url"] ?? DefaultApiUrl;
    }

    /// <summary>
    /// Reads <c>Backend:Enabled</c> from the environment files.
    /// Defaults to <c>true</c> when not configured.
    /// Set to <c>false</c> to prevent the client from launching a bundled
    /// backend process (useful when the API is installed separately or on
    /// another host).
    /// </summary>
    public static bool LoadBackendEnabled(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Backend:Enabled"];
        return value is null || !bool.TryParse(value, out var enabled) || enabled;
    }

    /// <summary>
    /// Reads <c>Gateway:Enabled</c> from the environment files.
    /// Defaults to <c>true</c> when not configured.
    /// Set to <c>false</c> to prevent the client from launching the
    /// gateway proxy.
    /// </summary>
    public static bool LoadGatewayEnabled(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Gateway:Enabled"];
        return value is null || !bool.TryParse(value, out var enabled) || enabled;
    }

    /// <summary>
    /// Reads <c>Gateway:Url</c> from the environment files, falling back
    /// to <see cref="DefaultGatewayUrl"/> when not configured.
    /// </summary>
    public static string LoadGatewayUrl(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        return config["Gateway:Url"] ?? DefaultGatewayUrl;
    }

    /// <summary>
    /// Reads <c>Processes:Persistent</c> from the environment files.
    /// Defaults to <c>false</c> when not configured.
    /// When <c>true</c>, backend and gateway processes remain alive
    /// after the frontend shuts down.
    /// </summary>
    public static bool LoadProcessesPersistent(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Processes:Persistent"];
        return value is not null && bool.TryParse(value, out var p) && p;
    }

    /// <summary>
    /// Reads <c>Processes:AutoStart</c> from the environment files.
    /// Defaults to <c>false</c> when not configured.
    /// When <c>true</c> on Windows, backend/gateway startup scripts
    /// are placed in <c>shell:startup</c> so they launch at login.
    /// </summary>
    public static bool LoadProcessesAutoStart(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Processes:AutoStart"];
        return value is not null && bool.TryParse(value, out var a) && a;
    }

    public static IConfigurationBuilder AddLocalEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = GetEnvironmentDirectory();
        EnsureEnvironmentFile(envDir);

        if (!Directory.Exists(envDir))
            return builder;

        var fileProvider = new PhysicalFileProvider(envDir, ExclusionFilters.None);

        builder.AddJsonFile(fileProvider, ".env", optional: true, reloadOnChange: false);

        if (isDevelopment)
            builder.AddJsonFile(fileProvider, ".dev.env", optional: true, reloadOnChange: false);

        return builder;
    }

    private static IConfiguration BuildConfiguration(bool isDevelopment)
    {
        var builder = new ConfigurationBuilder();
        builder.AddLocalEnvironment(isDevelopment);
        return builder.Build();
    }

    private static string GetEnvironmentDirectory() =>
        Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");

    private static void EnsureEnvironmentFile(string envDir)
    {
        var envFile = Path.Combine(envDir, ".env");
        if (File.Exists(envFile))
            return;

        try
        {
            Directory.CreateDirectory(envDir);
            File.WriteAllText(envFile, DefaultEnvContent);
        }
        catch
        {
            // Best-effort — read-only or restricted file system.
        }
    }
}
