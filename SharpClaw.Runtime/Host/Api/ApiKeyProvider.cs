using System.Security.Cryptography;
using System.Text;

using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host.Api;

/// <summary>
/// Generates and manages a per-session API key and gateway service token,
/// written to files with restrictive permissions. Only processes that can
/// read these files (i.e., running as the same user) can authenticate with
/// the localhost API.
/// </summary>
public sealed class ApiKeyProvider
{
    private readonly SharpClawInstancePaths _instancePaths;

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

    public ApiKeyProvider(SharpClawInstancePaths instancePaths)
    {
        _instancePaths = instancePaths;
        _instancePaths.EnsureDirectories();

        KeyFilePath = _instancePaths.ApiKeyFilePath;
        ApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        GatewayTokenFilePath = _instancePaths.GatewayTokenFilePath;
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
        DeleteFileIfOwned(KeyFilePath, ApiKey);
        DeleteFileIfOwned(GatewayTokenFilePath, GatewayToken);
    }

    private static void DeleteFileIfOwned(string path, string expectedContent)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var current = File.ReadAllText(path).Trim();
            var currentBytes = Encoding.UTF8.GetBytes(current);
            var expectedBytes = Encoding.UTF8.GetBytes(expectedContent);
            if (!CryptographicOperations.FixedTimeEquals(currentBytes, expectedBytes))
                return;

            File.Delete(path);
        }
        catch
        {
        }
    }
}
