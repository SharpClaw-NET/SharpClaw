using System.Security.Cryptography;

namespace SharpClaw.Shared.Security;

public static class PasswordHelper
{
    private const int SaltSize = 32;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static byte[] GenerateSalt()
        => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] Hash(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);

    public static bool Verify(string password, byte[] hash, byte[] salt)
        => CryptographicOperations.FixedTimeEquals(Hash(password, salt), hash);
}
