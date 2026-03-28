using System.Security.Cryptography;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Generates and manages a per-session API key and gateway service token,
/// written to files with restrictive permissions. Only processes that can
/// read these files (i.e., running as the same user) can authenticate with
/// the localhost API.
/// </summary>
public sealed class ApiKeyProvider
{
    public string ApiKey { get; }
    public string KeyFilePath { get; }

    /// <summary>
    /// A separate secret shared only with the gateway process. Presented
    /// via the <c>X-Gateway-Token</c> header, it allows the gateway to
    /// call core API endpoints without a user JWT while still proving its
    /// identity beyond the shared API key.
    /// </summary>
    public string GatewayToken { get; }
    public string GatewayTokenFilePath { get; }

    public ApiKeyProvider()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw");

        Directory.CreateDirectory(directory);

        KeyFilePath = Path.Combine(directory, ".api-key");
        ApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        GatewayTokenFilePath = Path.Combine(directory, ".gateway-token");
        GatewayToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        WriteSecureFile(KeyFilePath, ApiKey);
        WriteSecureFile(GatewayTokenFilePath, GatewayToken);
    }

    private static void WriteSecureFile(string path, string content)
    {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Cleanup()
    {
        try { File.Delete(KeyFilePath); } catch { /* best-effort */ }
        try { File.Delete(GatewayTokenFilePath); } catch { /* best-effort */ }
    }
}
