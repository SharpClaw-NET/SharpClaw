using System.Text.RegularExpressions;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Strips sensitive data patterns from exception messages before
/// exposing them to the model or storing them in <c>job.ResultData</c>.
/// The full, unsanitized exception is always logged via <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// </summary>
internal static partial class ExceptionSanitizer
{
    /// <summary>
    /// Sanitize an exception message for a module tool failure.
    /// Truncates to 200 characters, then scrubs file paths, IPs, GUIDs,
    /// and connection-string-like fragments.
    /// </summary>
    public static string Sanitize(string moduleId, string toolName, string message)
    {
        // Truncate first to bound work.
        var truncated = message.Length > 200 ? message[..200] + "…" : message;

        // Strip Windows/Unix file paths.
        truncated = FilePathRegex().Replace(truncated, "[path]");

        // Strip IPv4 addresses.
        truncated = Ipv4Regex().Replace(truncated, "[ip]");

        // Strip GUIDs.
        truncated = GuidRegex().Replace(truncated, "[id]");

        // Strip connection-string-like fragments.
        truncated = ConnStringRegex().Replace(truncated, "[connection]");

        return $"Module tool '{moduleId}.{toolName}' failed: {truncated}";
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s\""']+|/(?:usr|home|tmp|var|etc|opt|mnt)[^\s\""']*")]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:\d+)?\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"(Server|Data Source|Host|Password|User Id|Uid|Pwd)=[^;\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnStringRegex();
}
