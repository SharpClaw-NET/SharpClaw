using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Utils.Instances;
using SharpClaw.Utils.Security;

namespace SharpClaw.Infrastructure.Configuration;

/// <summary>
/// Loads environment configuration from the assembly-local active
/// <c>Environment/.env</c> file created from the portable plaintext
/// <c>Environment/.env.template</c>. Startup may encrypt the active file after
/// loading it, but it never mutates the template.
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

        return builder.AddLocalEnvironmentFrom(
            envDir,
            isDevelopment,
            instancePaths);
    }

    internal static IConfigurationBuilder AddLocalEnvironmentFrom(
        this IConfigurationBuilder builder,
        string envDir,
        bool isDevelopment,
        SharpClawInstancePaths? instancePaths = null)
    {
        instancePaths ??= TryResolveBackendInstancePathsFromEnvironment();

        EnsureEnvironmentTemplate(envDir, ".env.template");
        EnsureActiveEnvironmentFile(envDir, ".env", ".env.template");
        AddEnvFile(
            builder,
            envDir: envDir,
            fileName: ".env",
            templateFileName: ".env.template",
            instancePaths: instancePaths);

        if (isDevelopment)
        {
            EnsureEnvironmentTemplate(envDir, ".dev.env.template");
            EnsureActiveEnvironmentFile(envDir, ".dev.env", ".dev.env.template");
            AddEnvFile(
                builder,
                envDir: envDir,
                fileName: ".dev.env",
                templateFileName: ".dev.env.template",
                instancePaths: instancePaths);
        }

        return builder;
    }

    public static string ResolveActiveEnvFilePath(SharpClawInstancePaths? instancePaths = null) =>
        Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment",
            ".env");

    private static void AddEnvFile(
        IConfigurationBuilder builder,
        string envDir,
        string fileName,
        string templateFileName,
        SharpClawInstancePaths? instancePaths)
    {
        var activePath = Path.Combine(envDir, fileName);
        if (!File.Exists(activePath))
            return;

        if (!EncryptedEnvFile.IsEncryptedOnDisk(activePath))
        {
            try
            {
                AddPlaintextEnvFile(builder, activePath, instancePaths);
                return;
            }
            catch (Exception ex) when (IsUnreadableActiveFile(ex))
            {
                QuarantineUnreadableActiveFile(activePath);
                CopyTemplateToActive(envDir, fileName, templateFileName);
                AddPlaintextEnvFile(builder, activePath, instancePaths);
                return;
            }
        }

        var key = EncryptionKeyResolver.ResolveKey(instancePaths);
        try
        {
            var json = EncryptedEnvFile.ReadAsync(activePath, key, CancellationToken.None)
                .GetAwaiter().GetResult();
            AddJson(builder, json);
        }
        catch (Exception ex) when (IsUnreadableActiveFile(ex))
        {
            QuarantineUnreadableActiveFile(activePath);
            CopyTemplateToActive(envDir, fileName, templateFileName);
            AddPlaintextEnvFile(builder, activePath, instancePaths);
        }
    }

    private static void AddPlaintextEnvFile(
        IConfigurationBuilder builder,
        string activePath,
        SharpClawInstancePaths? instancePaths)
    {
        var json = File.ReadAllText(activePath);
        AddJson(builder, json);

        var key = EncryptionKeyResolver.ResolveKey(instancePaths);
        if (key is not null)
        {
            EncryptedEnvFile.WriteAsync(activePath, json, key, encrypt: true, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private static void AddJson(IConfigurationBuilder builder, string json)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        using (JsonDocument.Parse(json, options))
        {
        }

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        builder.AddJsonStream(stream);
    }

    private static void EnsureEnvironmentTemplate(
        string templateDir,
        string fileName)
    {
        var templatePath = Path.Combine(templateDir, fileName);
        if (File.Exists(templatePath) && new FileInfo(templatePath).Length > 0)
        {
            if (EncryptedEnvFile.IsEncryptedOnDisk(templatePath))
            {
                throw new InvalidOperationException(
                    $"Environment template '{templatePath}' is encrypted. Published templates must be plaintext and portable.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"Environment template '{templatePath}' is missing or empty. Published templates must be plaintext and portable.");
    }

    private static void EnsureActiveEnvironmentFile(
        string envDir,
        string activeFileName,
        string templateFileName)
    {
        var activePath = Path.Combine(envDir, activeFileName);
        if (File.Exists(activePath) && new FileInfo(activePath).Length > 0)
            return;

        CopyTemplateToActive(envDir, activeFileName, templateFileName);
    }

    private static void CopyTemplateToActive(
        string envDir,
        string activeFileName,
        string templateFileName)
    {
        var templatePath = Path.Combine(envDir, templateFileName);
        if (!File.Exists(templatePath) || new FileInfo(templatePath).Length == 0)
        {
            throw new InvalidOperationException(
                $"Environment template '{templatePath}' is missing or empty. Published templates must be plaintext and portable.");
        }

        if (EncryptedEnvFile.IsEncryptedOnDisk(templatePath))
        {
            throw new InvalidOperationException(
                $"Environment template '{templatePath}' is encrypted. Published templates must be plaintext and portable.");
        }

        Directory.CreateDirectory(envDir);
        File.Copy(templatePath, Path.Combine(envDir, activeFileName), overwrite: true);
    }

    private static void QuarantineUnreadableActiveFile(string activePath)
    {
        var quarantinePath = activePath + $".unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(activePath, quarantinePath, overwrite: true);
    }

    private static bool IsUnreadableActiveFile(Exception ex) =>
        ex is CryptographicException
        || ex is JsonException
        || ex is InvalidDataException
        || ex is FormatException
        || ex is ArgumentException
        || ex.InnerException is not null && IsUnreadableActiveFile(ex.InnerException);

    private static SharpClawInstancePaths? TryResolveBackendInstancePathsFromEnvironment()
    {
        var instanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
        var dataDir = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
        if (string.IsNullOrWhiteSpace(instanceRoot) && !string.IsNullOrWhiteSpace(dataDir))
            instanceRoot = Path.GetDirectoryName(Path.GetFullPath(dataDir));

        if (string.IsNullOrWhiteSpace(instanceRoot))
            return null;

        return new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot);
    }
}
