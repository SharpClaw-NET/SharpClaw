using System.Text;

namespace SharpClaw.Utils.Security;

/// <summary>
/// Reads and writes <c>.env</c> files that may be stored as AES-GCM
/// encrypted blobs on disk. Detection is automatic: version byte
/// <c>0x01</c> = encrypted, otherwise plaintext JSON.
/// </summary>
public static class EncryptedEnvFile
{
    /// <summary>
    /// Reads the <c>.env</c> file and returns the plaintext JSON content.
    /// Decrypts transparently if the file is an AES-GCM envelope.
    /// </summary>
    public static async Task<string> ReadAsync(string path, byte[]? encryptionKey, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length == 0)
            return string.Empty;

        if (bytes[0] == 0x01 && encryptionKey is not null)
        {
            var plain = ApiKeyEncryptor.DecryptBytes(bytes, encryptionKey);
            return Encoding.UTF8.GetString(plain);
        }

        // Plaintext JSON (legacy / encryption disabled).
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Writes JSON content to the <c>.env</c> file, encrypting the entire
    /// file when <paramref name="encrypt"/> is <c>true</c>.
    /// </summary>
    public static async Task WriteAsync(string path, string jsonContent, byte[]? encryptionKey, bool encrypt, CancellationToken ct = default)
    {
        byte[] bytes;
        if (encrypt && encryptionKey is not null)
        {
            var plain = Encoding.UTF8.GetBytes(jsonContent);
            bytes = ApiKeyEncryptor.EncryptBytes(plain, encryptionKey);
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(jsonContent);
        }

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    /// <summary>
    /// Returns <c>true</c> when the file on disk is an encrypted envelope.
    /// </summary>
    public static bool IsEncryptedOnDisk(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        return fs.ReadByte() == 0x01;
    }
}
