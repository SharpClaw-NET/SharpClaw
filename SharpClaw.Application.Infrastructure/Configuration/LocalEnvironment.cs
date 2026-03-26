using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace SharpClaw.Infrastructure.Configuration;

/// <summary>
/// Loads environment configuration from <c>Environment/.env</c> (always)
/// and <c>Environment/.dev.env</c> (development only) relative to the assembly location.
/// Creates a default <c>.env</c> if one does not exist.
/// </summary>
public static class LocalEnvironment
{
    private const string DefaultEnvContent =
        """
        {
          // SharpClaw Environment Configuration
          // Values here are loaded for all environments.

          "Admin": { "Username": "admin", "Password": "123456" },

          // Allow non-admin users to edit this file from the Uno client.
          //"EnvEditor": { "AllowNonAdmin": "false" }
        }
        """;

    public static IConfigurationBuilder AddLocalEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
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
