using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Utils.Security;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Loads gateway configuration from assembly-local active environment files.
/// Active files live beside their portable plaintext templates in
/// <c>Environment</c>: <c>.env</c> falls back to <c>.env.template</c>, and
/// development <c>.dev.env</c> falls back to <c>.dev.env.template</c>.
/// </summary>
public static class GatewayEnvironment
{
    public static IConfigurationBuilder AddGatewayEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(GatewayEnvironment).Assembly.Location)!,
            "Environment");

        return builder.AddGatewayEnvironmentFrom(envDir, isDevelopment);
    }

    internal static IConfigurationBuilder AddGatewayEnvironmentFrom(
        this IConfigurationBuilder builder,
        string envDir,
        bool isDevelopment)
    {
        EnsureEnvironmentTemplate(envDir, ".env.template");
        EnsureActiveEnvironmentFile(envDir, ".env", ".env.template");
        AddEnvFile(
            builder,
            envDir: envDir,
            activeFileName: ".env",
            templateFileName: ".env.template");

        if (isDevelopment)
        {
            EnsureEnvironmentTemplate(envDir, ".dev.env.template");
            EnsureActiveEnvironmentFile(envDir, ".dev.env", ".dev.env.template");
            AddEnvFile(
                builder,
                envDir: envDir,
                activeFileName: ".dev.env",
                templateFileName: ".dev.env.template");
        }

        return builder;
    }

    private static void AddEnvFile(
        IConfigurationBuilder builder,
        string envDir,
        string activeFileName,
        string templateFileName)
    {
        var activePath = Path.Combine(envDir, activeFileName);
        if (!File.Exists(activePath))
            return;

        if (EncryptedEnvFile.IsEncryptedOnDisk(activePath))
        {
            var key = EncryptionKeyResolver.ResolveKey();
            try
            {
                var json = EncryptedEnvFile.ReadAsync(activePath, key, CancellationToken.None)
                    .GetAwaiter().GetResult();
                AddJson(builder, json);
                return;
            }
            catch (Exception ex) when (IsUnreadableActiveFile(ex))
            {
                QuarantineUnreadableActiveFile(activePath);
                CopyTemplateToActive(envDir, activeFileName, templateFileName);
                AddPlaintextEnvFile(builder, activePath);
                return;
            }
        }

        try
        {
            AddPlaintextEnvFile(builder, activePath);
        }
        catch (Exception ex) when (IsUnreadableActiveFile(ex))
        {
            QuarantineUnreadableActiveFile(activePath);
            CopyTemplateToActive(envDir, activeFileName, templateFileName);
            AddPlaintextEnvFile(builder, activePath);
        }
    }

    private static void AddPlaintextEnvFile(IConfigurationBuilder builder, string activePath)
    {
        var json = File.ReadAllText(activePath);
        AddJson(builder, json);

        var key = EncryptionKeyResolver.ResolveKey();
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

        builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    private static void EnsureEnvironmentTemplate(string envDir, string templateFileName)
    {
        var templatePath = Path.Combine(envDir, templateFileName);
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
        ex is JsonException
        || ex is System.Security.Cryptography.CryptographicException
        || ex is InvalidDataException
        || ex is FormatException
        || ex is ArgumentException
        || ex.InnerException is not null && IsUnreadableActiveFile(ex.InnerException);
}
