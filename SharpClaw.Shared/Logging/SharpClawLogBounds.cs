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


public static class SharpClawLogBounds
{
    public const int MessageBytes = 64 * 1024;
    public const int TemplateBytes = 8 * 1024;
    public const int ExceptionBytes = 96 * 1024;
    public const int LevelBytes = 32;
    public const int EventNameBytes = 512;
    public const int ExceptionTypeBytes = 512;
    public const int CorrelationIdBytes = 256;
    public const int EventIdNameBytes = 256;
    public const int CategoryBytes = 512;
    public const int TraceIdBytes = 256;
    public const int SpanIdBytes = 256;
    public const int PropertyCount = 32;
    public const int TrustedPropertyCount = 4;
    public const int UserPropertyCount = PropertyCount - TrustedPropertyCount;
    public const int PropertyNameBytes = 128;
    public const int PropertyValueBytes = 4 * 1024;
    public const int TotalRecordBytes = 192 * 1024;
    public const int SidecarTailBytes = 64 * 1024;

    public static string TruncateUtf8(
        string value,
        int maximumBytes,
        out int originalBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        originalBytes = Encoding.UTF8.GetByteCount(value);
        if (originalBytes <= maximumBytes)
            return value;
        if (maximumBytes == 0)
            return string.Empty;

        var length = Math.Min(value.Length, maximumBytes);
        while (length > 0
               && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maximumBytes)
        {
            length -= Math.Max(
                1,
                (Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) - maximumBytes) / 4);
        }

        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..Math.Max(0, length)];
    }
}
