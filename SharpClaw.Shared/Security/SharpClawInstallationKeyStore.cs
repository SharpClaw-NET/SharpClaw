using System.Security.Cryptography;

using Supprocom.Secrets;

namespace SharpClaw.Shared.Security;

/// <summary>
/// Connects SharpClaw's existing installation-key contract to the package
/// installation-key store. The package owns file creation, permissions, and
/// legacy Base64-to-raw migration; this adapter only preserves SharpClaw's
/// existing environment-variable override and byte representation.
/// </summary>
public sealed class SharpClawInstallationKeyStore : IInstallationKeyStore
{
    private const string EnvironmentVariableName = "SHARPCLAW_ENCRYPTION_KEY";
    private const int KeySize = 32;
    private readonly IInstallationKeyStore fileStore;

    public SharpClawInstallationKeyStore(string path)
    {
        fileStore = new FileInstallationKeyStore(path);
    }

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = GetConfiguredKeyOrNull();
        return configured ?? await fileStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadExistingKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = GetConfiguredKeyOrNull();
        return configured ?? await fileStore.ReadExistingKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the validated environment override in the Base64 representation
    /// used by existing SharpClaw encryption consumers, or <c>null</c> when the
    /// override is not configured.
    /// </summary>
    public static string? GetConfiguredKeyBase64OrNull()
    {
        var key = GetConfiguredKeyOrNull();
        if (key is null)
            return null;

        try
        {
            return Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[]? GetConfiguredKeyOrNull()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (configured is null)
            return null;

        if (configured.Length == 0 || configured.Any(char.IsWhiteSpace))
            throw InvalidConfiguredKey();

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException exception)
        {
            throw InvalidConfiguredKey(exception);
        }

        if (key.Length != KeySize ||
            !string.Equals(Convert.ToBase64String(key), configured, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(key);
            throw InvalidConfiguredKey();
        }

        return key;
    }

    private static SupprocomSecretsException InvalidConfiguredKey(Exception? inner = null) =>
        inner is null
            ? new(
                "InvalidInstallationKey",
                $"{EnvironmentVariableName} must be canonical Base64 text for exactly 32 bytes.")
            : new(
                "InvalidInstallationKey",
                $"{EnvironmentVariableName} must be canonical Base64 text for exactly 32 bytes.",
                inner);
}
