using System.Security.Cryptography;

using SharpClaw.Shared.Instances;

namespace SharpClaw.Shared.Security;

/// <summary>
/// Generates and persists cryptographic keys in the local application data directory.
/// Keys survive across restarts, unlike in-memory random generation.
/// </summary>
public static class PersistentKeyStore
{
    private static readonly string KeyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClaw");

    /// <summary>
    /// Returns the base64-encoded key for the given name, generating and
    /// persisting a new 256-bit key if one doesn't already exist.
    /// </summary>
    public static string GetOrCreate(string keyName)
    {
        Directory.CreateDirectory(KeyDirectory);

        var filePath = Path.Combine(KeyDirectory, $".{keyName}");

        return GetOrCreateFromFilePath(filePath, keyName);
    }

    /// <summary>
    /// Returns the base64-encoded key for the given name within the specified
    /// instance root, generating and persisting a new 256-bit key if one does
    /// not already exist.
    /// </summary>
    public static string GetOrCreate(string keyName, SharpClawInstancePaths instancePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentNullException.ThrowIfNull(instancePaths);

        instancePaths.EnsureDirectories();
        return GetOrCreateFromFilePath(instancePaths.GetSecretFilePath(keyName), keyName);
    }

    private static string GetOrCreateFromFilePath(string filePath, string keyName)
    {
        if (string.Equals(keyName, "encryption-key", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateInstallationKeyAsBase64(filePath);

        if (File.Exists(filePath))
            return File.ReadAllText(filePath).Trim();

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(filePath, key);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return key;
    }

    private static string GetOrCreateInstallationKeyAsBase64(string filePath)
    {
        byte[] key = new SharpClawInstallationKeyStore(filePath)
            .GetOrCreateKeyAsync()
            .GetAwaiter()
            .GetResult();
        try
        {
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
