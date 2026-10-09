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


internal static class SharpClawLogRedactor
{
    private static readonly Regex Authorization = new(
        @"(authorization\s*[:=]\s*(?:[\x22']?bearer\s+)?[\x22']?)[^\s,;}'\x22]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Secrets = new(
        @"((?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|cookie|client[_-]?secret|connection[_-]?string|encryption[_-]?key)\s*[:=]\s*(?:[\x22']?bearer\s+)?[\x22']?)[^\s,;}'\x22]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UriCredentials = new(
        @"(https?://[^/@\s:]+:)[^/@\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SensitiveLabeledValue = new(
        @"((?:body|request[_-]?body|response[_-]?body|prompt|model[_-]?response)\s*[:=]\s*)(?:[\x22']?)[^\s,;}'\x22]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UriQuery = new(
        @"((?:https?://|/)[^\s?]*\?)[^\s,;}'\x22]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Redact(string value)
    {
        var result = Authorization.Replace(value, "$1[REDACTED]");
        result = Secrets.Replace(result, "$1[REDACTED]");
        result = SensitiveLabeledValue.Replace(result, "$1[REDACTED]");
        result = UriCredentials.Replace(result, "$1[REDACTED]@");
        return UriQuery.Replace(result, "$1[REDACTED]");
    }

    public static bool IsSecretPropertyName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesstoken", StringComparison.Ordinal)
            || normalized.Contains("refreshtoken", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("clientsecret", StringComparison.Ordinal)
            || normalized.Contains("connectionstring", StringComparison.Ordinal)
            || normalized.Contains("encryptionkey", StringComparison.Ordinal)
            || normalized is "body" or "requestbody" or "responsebody"
            || normalized is "prompt" or "modelresponse"
            || normalized is "uri" or "url" or "query" or "querystring"
            || normalized is "headers" or "requestheaders" or "responseheaders"
            || normalized.Equals("secret", StringComparison.Ordinal);
    }
}
