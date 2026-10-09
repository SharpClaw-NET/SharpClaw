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


public sealed record SharpClawLoggingOptions
{
    public const string SectionPath = "Logging";

    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;
    public LogEventLevel MicrosoftMinimumLevel { get; init; } = LogEventLevel.Warning;
    public LogEventLevel AspNetCoreMinimumLevel { get; init; } = LogEventLevel.Warning;
    public LogEventLevel EntityFrameworkCoreMinimumLevel { get; init; } = LogEventLevel.Warning;
    public LogEventLevel UnoMinimumLevel { get; init; } = LogEventLevel.Warning;
    public bool ConsoleEnabled { get; init; }
    public bool RequestLoggingEnabled { get; init; } = true;
    public int QueueCapacity { get; init; } = 4096;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static SharpClawLoggingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var queueCapacity = ReadInt(configuration, "Logging:QueueCapacity", 4096);
        if (queueCapacity is < 16 or > 1_000_000)
            throw new InvalidOperationException("Logging:QueueCapacity must be between 16 and 1000000.");

        var flushMilliseconds = ReadInt(
            configuration,
            "Logging:FlushIntervalMilliseconds",
            1000);
        if (flushMilliseconds is < 10 or > 60_000)
        {
            throw new InvalidOperationException(
                "Logging:FlushIntervalMilliseconds must be between 10 and 60000.");
        }

        var minimumLevel = ReadLevel(
            configuration,
            "Logging:MinimumLevel",
            LogEventLevel.Information,
            "Logging:Serilog:MinimumLevel");
        if (!HasValue(configuration, "Logging:MinimumLevel")
            && ReadOptionalBool(configuration, "Logging:Serilog:Enabled") is false)
        {
            minimumLevel = LogEventLevel.Fatal;
        }

        return new SharpClawLoggingOptions
        {
            MinimumLevel = minimumLevel,
            MicrosoftMinimumLevel = ReadLevel(
                configuration,
                "Logging:Overrides:Microsoft",
                LogEventLevel.Warning,
                "Logging:Serilog:MicrosoftMinimumLevel"),
            AspNetCoreMinimumLevel = ReadLevel(
                configuration,
                "Logging:Overrides:Microsoft.AspNetCore",
                LogEventLevel.Warning,
                "Logging:Serilog:AspNetCoreMinimumLevel"),
            EntityFrameworkCoreMinimumLevel = ReadLevel(
                configuration,
                "Logging:Overrides:Microsoft.EntityFrameworkCore",
                LogEventLevel.Warning,
                "Logging:Serilog:EntityFrameworkCoreMinimumLevel"),
            UnoMinimumLevel = ReadLevel(
                configuration,
                "Logging:Overrides:Uno",
                LogEventLevel.Warning,
                "Logging:Serilog:UnoMinimumLevel"),
            ConsoleEnabled = ReadBool(
                configuration,
                "Logging:ConsoleEnabled",
                false,
                "Logging:Serilog:ConsoleEnabled"),
            RequestLoggingEnabled = ReadBool(
                configuration,
                "Logging:RequestLoggingEnabled",
                true,
                "Logging:Serilog:RequestLoggingEnabled"),
            QueueCapacity = queueCapacity,
            FlushInterval = TimeSpan.FromMilliseconds(flushMilliseconds),
        };
    }

    private static LogEventLevel ReadLevel(
        IConfiguration configuration,
        string key,
        LogEventLevel fallback,
        string? legacyKey = null)
    {
        var current = configuration[key];
        if (HasValue(configuration, key))
        {
            if (Enum.TryParse<LogEventLevel>(current, ignoreCase: true, out var value))
                return value;
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be a Serilog level such as Information, Warning, Error, or Fatal.");
        }

        return legacyKey is not null
               && Enum.TryParse<LogEventLevel>(
                   configuration[legacyKey],
                   ignoreCase: true,
                   out var legacyValue)
            ? legacyValue
            : fallback;
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string key,
        bool fallback,
        string? legacyKey = null)
    {
        var current = configuration[key];
        if (HasValue(configuration, key))
        {
            if (bool.TryParse(current, out var value))
                return value;
            throw new InvalidOperationException($"Configuration value '{key}' must be true or false.");
        }

        return legacyKey is not null
               && bool.TryParse(configuration[legacyKey], out var legacyValue)
            ? legacyValue
            : fallback;
    }

    private static bool? ReadOptionalBool(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        return bool.TryParse(raw, out var value) ? value : null;
    }

    private static bool HasValue(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]);

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidOperationException($"Configuration value '{key}' must be an integer.");
    }
}
