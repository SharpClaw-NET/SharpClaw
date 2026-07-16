using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Shared.Security;

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
    public static byte[]? ResolveKey(SharpClawInstancePaths? instancePaths = null)
    {
        try
        {
            var configured = SharpClawInstallationKeyStore.GetConfiguredKeyOrNull();
            if (configured is not null)
                return configured.Length == 32 ? configured : null;

            var keyBase64 = instancePaths is null
                ? PersistentKeyStore.GetOrCreate("encryption-key")
                : PersistentKeyStore.GetOrCreate("encryption-key", instancePaths);
            var decoded = Convert.FromBase64String(keyBase64);
            return decoded.Length == 32 ? decoded : null;
        }
        catch (SupprocomSecretsException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
