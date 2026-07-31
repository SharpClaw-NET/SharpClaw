using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimeCertificateHash
{
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException(
                "The Runtime bridge certificate must contain an ECDSA public key.");
        return Compute(publicKey);
    }

    public static string Compute(ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var publicKeyInfo = publicKey.ExportSubjectPublicKeyInfo();
        try
        {
            return Convert.ToBase64String(SHA256.HashData(publicKeyInfo))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKeyInfo);
        }
    }
}
