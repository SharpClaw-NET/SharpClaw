using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using SharpClaw.Utils.Security;

namespace SharpClaw.Infrastructure.Configuration;

/// <summary>
/// Loads environment configuration from <c>Environment/.env</c> (always)
/// and <c>Environment/.dev.env</c> (development only) relative to the assembly location.
/// Creates a default <c>.env</c> if it does not exist.
/// Supports transparent decryption of AES-GCM encrypted <c>.env</c> files
/// and auto-locks plaintext files on first read.
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

        AddEnvFile(builder, envDir, ".env");

        if (isDevelopment)
            AddEnvFile(builder, envDir, ".dev.env");

        return builder;
    }

    private static void AddEnvFile(IConfigurationBuilder builder, string envDir, string fileName)
    {
        var path = Path.Combine(envDir, fileName);
        if (!File.Exists(path))
            return;

        if (EncryptedEnvFile.IsEncryptedOnDisk(path))
        {
            // Encrypted — decrypt into memory, load as JSON stream.
            var key = EncryptionKeyResolver.ResolveKey();
            var json = EncryptedEnvFile.ReadAsync(path, key, CancellationToken.None)
                .GetAwaiter().GetResult(); // Sync OK: startup, single call
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            builder.AddJsonStream(stream);
        }
        else
        {
            // Plaintext — load into memory, then auto-lock (encrypt in-place)
            // so the file doesn't remain as readable text on disk.
            var key = EncryptionKeyResolver.ResolveKey();
            var json = File.ReadAllText(path);
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            builder.AddJsonStream(stream);

            if (key is not null)
            {
                // Auto-lock: encrypt the plaintext file in-place immediately.
                EncryptedEnvFile.WriteAsync(path, json, key, encrypt: true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
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
}
