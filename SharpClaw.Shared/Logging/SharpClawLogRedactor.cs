using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpClaw.Shared.Logging;


internal static partial class SharpClawLogRedactor
{
    private const RegexOptions RedactionOptions = RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    [GeneratedRegex(@"(?<prefix>authorization\s*[:=]\s*(?:[\x22']?bearer\s+)?[\x22']?)[^\s,;}'\x22]+",
        RedactionOptions, 1000)]
    private static partial Regex Authorization();

    [GeneratedRegex(@"(?<prefix>(?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|cookie|client[_-]?secret|connection[_-]?string|encryption[_-]?key)\s*[:=]\s*(?:[\x22']?bearer\s+)?[\x22']?)[^\s,;}'\x22]+",
        RedactionOptions, 1000)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"(?<prefix>https?://[^/@\s:]+:)[^/@\s]+@", RedactionOptions, 1000)]
    private static partial Regex UriCredentials();

    [GeneratedRegex(@"(?<prefix>(?:body|request[_-]?body|response[_-]?body|prompt|model[_-]?response)\s*[:=]\s*)(?:[\x22']?)[^\s,;}'\x22]+",
        RedactionOptions, 1000)]
    private static partial Regex SensitiveLabeledValue();

    [GeneratedRegex(@"(?<prefix>(?:https?://|/)[^\s?]*\?)[^\s,;}'\x22]+", RedactionOptions, 1000)]
    private static partial Regex UriQuery();

    public static string Redact(string value)
    {
        try
        {
            var result = Authorization().Replace(value, "${prefix}[REDACTED]");
            result = Secrets().Replace(result, "${prefix}[REDACTED]");
            result = SensitiveLabeledValue().Replace(result, "${prefix}[REDACTED]");
            result = UriCredentials().Replace(result, "${prefix}[REDACTED]@");
            return UriQuery().Replace(result, "${prefix}[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            // A redaction budget failure cannot make the original value safe to log.
            return "[REDACTED: matching budget exceeded]";
        }
    }

    public static bool IsSecretPropertyName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("accesstoken", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("refreshtoken", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("password", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("clientsecret", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("encryptionkey", StringComparison.OrdinalIgnoreCase)
            || IsSensitiveLabel(normalized);
    }

    private static bool IsSensitiveLabel(string value) =>
        string.Equals(value, "body", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "requestbody", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "responsebody", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "prompt", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "modelresponse", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "uri", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "query", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "querystring", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "headers", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "requestheaders", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "responseheaders", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "secret", StringComparison.OrdinalIgnoreCase);
}
