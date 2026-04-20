namespace SharpClaw.Utils.Security;

/// <summary>
/// Resolves the application encryption key from configuration or the
/// persistent key store. Usable before DI is built (e.g. during config loading).
/// </summary>
public static class EncryptionKeyResolver
{
    /// <summary>
    /// Returns the 256-bit encryption key bytes, or <c>null</c> if no key
    /// can be resolved (should not happen in normal operation — the
    /// persistent key store auto-generates one).
    /// </summary>
    public static byte[]? ResolveKey()
    {
        try
        {
            var keyBase64 = Environment.GetEnvironmentVariable("SHARPCLAW_ENCRYPTION_KEY")
                ?? PersistentKeyStore.GetOrCreate("encryption-key");
            var key = Convert.FromBase64String(keyBase64);
            return key.Length == 32 ? key : null;
        }
        catch
        {
            return null;
        }
    }
}
