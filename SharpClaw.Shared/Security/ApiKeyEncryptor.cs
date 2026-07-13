using System.Security.Cryptography;
using System.Text;

namespace SharpClaw.Shared.Security;

public static class ApiKeyEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>
    /// Minimum envelope size for <see cref="EncryptBytes"/>:
    /// 1 (version) + 12 (nonce) + 0 (cipher) + 16 (tag) = 29 bytes.
    /// </summary>
    public const int MinEnvelopeSize = 1 + NonceSize + TagSize;

    public static string Encrypt(string plainText, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Format: nonce + ciphertext + tag
        var result = new byte[NonceSize + cipherBytes.Length + TagSize];
        nonce.CopyTo(result, 0);
        cipherBytes.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string encryptedText, byte[] key)
    {
        var data = Convert.FromBase64String(encryptedText);

        var nonce = data[..NonceSize];
        var tag = data[^TagSize..];
        var cipherBytes = data[NonceSize..^TagSize];
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Encrypts raw bytes into a versioned envelope: [0x01][nonce 12][cipher N][tag 16].
    /// Used for on-disk JSON file encryption.
    /// </summary>
    public static byte[] EncryptBytes(ReadOnlySpan<byte> plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        var result = new byte[1 + NonceSize + cipher.Length + TagSize];
        result[0] = 0x01;
        nonce.CopyTo(result.AsSpan(1));
        cipher.CopyTo(result.AsSpan(1 + NonceSize));
        tag.CopyTo(result.AsSpan(1 + NonceSize + cipher.Length));
        return result;
    }

    /// <summary>
    /// Decrypts a versioned envelope produced by <see cref="EncryptBytes"/>.
    /// </summary>
    public static byte[] DecryptBytes(ReadOnlySpan<byte> envelope, byte[] key)
    {
        if (envelope.Length < MinEnvelopeSize)
            throw new ArgumentException(
                $"Encrypted envelope too short ({envelope.Length} bytes, minimum {MinEnvelopeSize}).");

        if (envelope[0] != 0x01)
            throw new ArgumentException(
                $"Unsupported envelope version 0x{envelope[0]:X2}.");

        var nonce = envelope.Slice(1, NonceSize);
        var tag = envelope[^TagSize..];
        var cipher = envelope.Slice(1 + NonceSize, envelope.Length - 1 - NonceSize - TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    /// <summary>
    /// Attempts to decrypt a stored value. If the value does not appear to be
    /// encrypted (e.g. plaintext API key after toggling EncryptProviderKeys off),
    /// returns it as-is. Handles mixed encrypted/plaintext state gracefully.
    /// </summary>
    public static string DecryptOrPassthrough(string storedValue, byte[] key)
    {
        if (!IsLikelyEncrypted(storedValue))
            return storedValue;

        try
        {
            return Decrypt(storedValue, key);
        }
        catch
        {
            return storedValue;
        }
    }

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Heuristic: encrypted values are valid Base64 of at least
    /// NonceSize + TagSize bytes. Plaintext API keys (e.g. "sk-...")
    /// will typically fail Base64 decode or be too short.
    /// </summary>
    private static bool IsLikelyEncrypted(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= NonceSize + TagSize;
        }
        catch
        {
            return false;
        }
    }
}
