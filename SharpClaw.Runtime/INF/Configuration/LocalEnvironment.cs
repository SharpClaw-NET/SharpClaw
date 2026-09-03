using Microsoft.Extensions.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Runtime.INF.Configuration;

/// <summary>
/// Adds the Runtime Host's assembly-local dotenv files through
/// Supprocom.Secrets. The package owns parsing, import, protection, and
/// recovery; SharpClaw supplies only its directory and installation-key path.
/// </summary>
public static class LocalEnvironment
{
    public static IConfigurationBuilder AddLocalEnvironment(
        this IConfigurationBuilder builder,
        bool isDevelopment = false,
        SharpClawInstancePaths? instancePaths = null)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");

        return builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment, instancePaths));
    }

    internal static IConfigurationBuilder AddLocalEnvironmentFrom(
        this IConfigurationBuilder builder,
        string envDir,
        bool isDevelopment,
        SharpClawInstancePaths? instancePaths = null)
    {
        instancePaths ??= TryResolveBackendInstancePathsFromEnvironment();
        return builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment, instancePaths));
    }

    /// <summary>
    /// Creates the package options used by Runtime startup and the protected
    /// document service. This is the SharpClaw-specific path and precedence
    /// policy; generic file behavior remains in Supprocom.Secrets.
    /// </summary>
    public static SupprocomSecretsOptions CreateSecretsOptions(
        string envDir,
        bool isDevelopment,
        SharpClawInstancePaths? instancePaths = null)
    {
        instancePaths ??= TryResolveBackendInstancePathsFromEnvironment();
        var installationKeyPath = ResolveInstallationKeyPath(instancePaths);

        return new SupprocomSecretsOptions
        {
            EnvironmentName = isDevelopment ? "Development" : "Production",
            FileOverridesProcessEnvironment = true,
            File =
            {
                Directory = envDir,
                ActiveName = ".env",
                DevelopmentName = ".dev.env",
                TemplateName = ".env.template",
                DevelopmentTemplateName = ".dev.env.template",
                Import = SecretFileImport.JsonWithCommentsOnce,
                DevelopmentComposition = SecretFileComposition.Overlay,
                Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyPath = installationKeyPath,
                InstallationKeyStore = new SharpClawInstallationKeyStore(installationKeyPath)
            }
        };
    }

    public static string ResolveActiveEnvFilePath() =>
        Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment",
            ".env");

    private static string ResolveInstallationKeyPath(SharpClawInstancePaths? instancePaths) =>
        instancePaths?.GetSecretFilePath("encryption-key")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw",
            ".encryption-key");

    private static SharpClawInstancePaths? TryResolveBackendInstancePathsFromEnvironment()
    {
        var instanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
        var dataDir = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
        if (string.IsNullOrWhiteSpace(instanceRoot) && !string.IsNullOrWhiteSpace(dataDir))
            instanceRoot = Path.GetDirectoryName(Path.GetFullPath(dataDir));

        return string.IsNullOrWhiteSpace(instanceRoot)
            ? null
            : new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
    }
}
