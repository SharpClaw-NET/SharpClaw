namespace Mk8.Shell.Safety;

/// <summary>
/// SSRF protection for mk8.shell HTTP verbs.
/// </summary>
public static class Mk8UrlSanitizer
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "https", "http" };

    private static readonly HashSet<string> BlockedHosts =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
        "metadata.internal",
        "169.254.169.254",
    };

    private static readonly HashSet<int> AllowedPorts = [80, 443, -1];

    /// <summary>
    /// Blocked hostname patterns for DNS/ping operations.
    /// Prevents probing internal infrastructure.
    /// </summary>
    private static readonly string[] BlockedHostSuffixes =
    [
        ".internal", ".local", ".corp", ".lan", ".intranet", ".private",
    ];

    /// <summary>
    /// Validates a hostname for use with NetPing/NetDns. Blocks:
    /// private/metadata hosts, IP literals, internal suffixes.
    /// </summary>
    public static void ValidateHostname(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        if (hostname.Length > 253)
            throw new Mk8UrlViolationException(hostname,
                "Hostname exceeds maximum length of 253 characters.");

        // Block IP address literals — force DNS resolution only.
        if (System.Net.IPAddress.TryParse(hostname, out _))
            throw new Mk8UrlViolationException(hostname,
                "IP address literals are not allowed. Use a hostname.");

        // Block known metadata/internal hosts.
        if (BlockedHosts.Contains(hostname))
            throw new Mk8UrlViolationException(hostname,
                $"Host '{hostname}' is blocked.");

        // Block internal TLD patterns.
        foreach (var suffix in BlockedHostSuffixes)
        {
            if (hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new Mk8UrlViolationException(hostname,
                    $"Hostname with suffix '{suffix}' is blocked.");
        }

        // Validate charset: alphanumeric, hyphens, dots only.
        foreach (var ch in hostname)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '.')
                throw new Mk8UrlViolationException(hostname,
                    $"Invalid character '{ch}' in hostname.");
        }
    }

    public static Uri Validate(string rawUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawUrl);

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            throw new Mk8UrlViolationException(rawUrl, "Not a valid absolute URL.");

        if (!AllowedSchemes.Contains(uri.Scheme))
            throw new Mk8UrlViolationException(rawUrl,
                $"Scheme '{uri.Scheme}' is not allowed. Use https or http.");

        if (BlockedHosts.Contains(uri.Host))
            throw new Mk8UrlViolationException(rawUrl,
                $"Host '{uri.Host}' is blocked.");

        if (!AllowedPorts.Contains(uri.Port))
            throw new Mk8UrlViolationException(rawUrl,
                $"Port {uri.Port} is not allowed. Use 80 or 443.");

        if (uri.UserInfo.Length > 0)
            throw new Mk8UrlViolationException(rawUrl,
                "URLs with embedded credentials are not allowed.");

        return uri;
    }

    public static bool IsPrivateOrReserved(System.Net.IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => IsPrivateIPv4(bytes),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => IsPrivateIPv6(bytes),
            _ => true
        };
    }

    private static bool IsPrivateIPv4(byte[] bytes) =>
        bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168)
        || (bytes[0] == 169 && bytes[1] == 254)
        || bytes[0] == 127
        || bytes[0] == 0;

    private static bool IsPrivateIPv6(byte[] bytes) =>
        bytes[0] == 0xFD
        || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        || bytes.All(b => b == 0)
        || (bytes.Take(15).All(b => b == 0) && bytes[15] == 1);
}
