using Microsoft.Extensions.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using SharpClaw.Services;
using Supprocom.Secrets;

namespace SharpClaw.Configuration;

/// <summary>
/// Adds the Client.Uno assembly-local dotenv files through Supprocom.Secrets.
/// Client-specific defaults remain here; parsing, protection, import, and
/// recovery belong to the package.
/// </summary>
public static class LocalEnvironment
{
    public const string DefaultApiUrl = "http://127.0.0.1:48923";
    public const string DefaultGatewayUrl = "http://0.0.0.0:48924";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public static string LoadApiUrl(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        return config["Api:Url"] ?? DefaultApiUrl;
    }

    public static bool LoadBackendEnabled(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Backend:Enabled"];
        return value is null || !bool.TryParse(value, out var enabled) || enabled;
    }

    public static bool LoadGatewayEnabled(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Gateway:Enabled"];
        return value is not null && bool.TryParse(value, out var enabled) && enabled;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public static string LoadGatewayUrl(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        return config["Gateway:Url"] ?? DefaultGatewayUrl;
    }

    public static bool LoadProcessesPersistent(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Processes:Persistent"];
        return value is not null && bool.TryParse(value, out var persistent) && persistent;
    }

    public static bool LoadProcessesAutoStart(bool isDevelopment = false)
    {
        var config = BuildConfiguration(isDevelopment);
        var value = config["Processes:AutoStart"];
        return value is not null && bool.TryParse(value, out var autoStart) && autoStart;
    }

    public static IConfigurationBuilder AddLocalEnvironment(
        this IConfigurationBuilder builder,
        bool isDevelopment = false,
        SharpClawInstancePaths? instancePaths = null)
    {
        var assemblyDirectory = GetEnvironmentDirectory();
        string envDir;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SharpClawEnvironmentDirectory.OverrideVariable)))
            envDir = SharpClawEnvironmentDirectory.Resolve(assemblyDirectory);
        else if (isDevelopment)
            envDir = assemblyDirectory;
        else
        {
            instancePaths ??= new FrontendInstanceService().Paths;
            envDir = SharpClawEnvironmentDirectory.Prepare(assemblyDirectory, instancePaths.ConfigDirectory);
        }
        return builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment, instancePaths));
    }

    internal static IConfigurationBuilder AddLocalEnvironmentFrom(
        this IConfigurationBuilder builder,
        string envDir,
        bool isDevelopment,
        SharpClawInstancePaths? instancePaths = null,
        string? installationKeyPath = null) =>
        builder.AddSupprocomSecrets(
            CreateSecretsOptions(envDir, isDevelopment, instancePaths, installationKeyPath));

    internal static SupprocomSecretsOptions CreateSecretsOptions(
        string envDir,
        bool isDevelopment,
        SharpClawInstancePaths? instancePaths = null,
        string? installationKeyPath = null) =>
        new()
        {
            EnvironmentName = isDevelopment ? "Development" : "Production",
            FileOverridesProcessEnvironment = string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(SharpClawEnvironmentDirectory.OverrideVariable)),
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
                InstallationKeyPath = installationKeyPath
                    ?? instancePaths?.GetSecretFilePath("encryption-key")
                    ?? ResolveDefaultInstallationKeyPath(),
                InstallationKeyStore = new SharpClawInstallationKeyStore(
                    installationKeyPath
                    ?? instancePaths?.GetSecretFilePath("encryption-key")
                    ?? ResolveDefaultInstallationKeyPath())
            }
        };

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

    private static string ResolveDefaultInstallationKeyPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw",
            ".encryption-key");
}
