using Microsoft.Extensions.Configuration;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Adds the Gateway's assembly-local dotenv files through Supprocom.Secrets.
/// SharpClaw supplies only the Gateway directory and its existing shared
/// installation-key location; the package owns all file behavior.
/// </summary>
public static class GatewayEnvironment
{
    public static IConfigurationBuilder AddGatewayEnvironment(
        this IConfigurationBuilder builder,
        bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(GatewayEnvironment).Assembly.Location)!,
            "Environment");

        return builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment));
    }

    internal static IConfigurationBuilder AddGatewayEnvironmentFrom(
        this IConfigurationBuilder builder,
        string envDir,
        bool isDevelopment,
        string? installationKeyPath = null) =>
        builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment, installationKeyPath));

    internal static SupprocomSecretsOptions CreateSecretsOptions(
        string envDir,
        bool isDevelopment,
        string? installationKeyPath = null) =>
        new()
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
                InstallationKeyPath = installationKeyPath ?? ResolveInstallationKeyPath(),
                InstallationKeyStore = new SharpClawInstallationKeyStore(
                    installationKeyPath ?? ResolveInstallationKeyPath())
            }
        };

    private static string ResolveInstallationKeyPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw",
            ".encryption-key");
}
