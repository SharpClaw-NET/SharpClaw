using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace SharpClaw.Infrastructure.Configuration;

/// <summary>
/// Loads environment configuration from <c>Environment/.env</c> (always),
/// <c>Environment/.modules.env</c> (always, machine-managed module state),
/// and <c>Environment/.dev.env</c> (development only) relative to the assembly location.
/// Creates defaults for <c>.env</c> and <c>.modules.env</c> if they do not exist.
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

    private const string DefaultModulesEnvContent =
        """
        {
          "Modules": {
            "sharpclaw_computer_use": false,
            "sharpclaw_dangerous_shell": false,
            "sharpclaw_mk8shell": false,
            "sharpclaw_office_apps": false,
            "sharpclaw_vs2026_editor": false,
            "sharpclaw_vscode_editor": false
          }
        }
        """;

    public static IConfigurationBuilder AddLocalEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");

        EnsureEnvironmentFile(envDir);
        EnsureModulesEnvironmentFile(envDir);

        if (!Directory.Exists(envDir))
            return builder;

        // PhysicalFileProvider defaults to ExclusionFilters.Sensitive which
        // excludes dot-prefixed files (.env, .dev.env). Use None so the
        // configuration system can see them.
        var fileProvider = new PhysicalFileProvider(envDir, ExclusionFilters.None);

        builder.AddJsonFile(fileProvider, ".env", optional: true, reloadOnChange: false);
        builder.AddJsonFile(fileProvider, ".modules.env", optional: true, reloadOnChange: false);

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
            // Best-effort — read-only or restricted file system.
        }
    }

    private static void EnsureModulesEnvironmentFile(string envDir)
    {
        var modulesEnvFile = Path.Combine(envDir, ".modules.env");
        if (File.Exists(modulesEnvFile) && new FileInfo(modulesEnvFile).Length > 0)
            return;

        try
        {
            Directory.CreateDirectory(envDir);
            File.WriteAllText(modulesEnvFile, DefaultModulesEnvContent);
        }
        catch
        {
            // Best-effort — read-only or restricted file system.
        }
    }
}
