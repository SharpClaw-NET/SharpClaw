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

public enum SharpClawModuleHostKind
{
    RuntimeInProcess,
    RuntimeSidecar,
    Gateway,
}

public sealed record SharpClawModuleLogContext(
    string ModuleId,
    string? ModuleVersion,
    SharpClawModuleHostKind HostKind,
    Guid BootId);

public static class SharpClawLogOwnership
{
    private static readonly AsyncLocal<SharpClawModuleLogContext?> CurrentContext = new();

    public static SharpClawModuleLogContext? Current => CurrentContext.Value;

    public static IDisposable Push(SharpClawModuleLogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(SharpClawModuleLogContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}

public sealed class SharpClawModuleLoggerFactory(
    ILoggerFactory hostFactory,
    SharpClawModuleLogContext context) : ILoggerFactory
{
    public MsLogger CreateLogger(string categoryName) =>
        new ModuleLogger(hostFactory.CreateLogger(categoryName), context);

    public void AddProvider(ILoggerProvider provider) =>
        throw new InvalidOperationException(
            "Module logger factories cannot add providers to the host logging pipeline.");

    public void Dispose()
    {
    }

    private sealed class ModuleLogger(
        MsLogger inner,
        SharpClawModuleLogContext context) : MsLogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            using var ownership = SharpClawLogOwnership.Push(context);
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

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
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

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

    public static string Redact(string value)
    {
        var result = Authorization.Replace(value, "$1[REDACTED]");
        result = Secrets.Replace(result, "$1[REDACTED]");
        return UriCredentials.Replace(result, "$1[REDACTED]@");
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
            || normalized.Equals("secret", StringComparison.Ordinal);
    }
}

internal static class SharpClawLogNormalizer
{
    private static readonly string[] TrustedPropertyNames =
    [
        "SharpClaw.ModuleId",
        "SharpClaw.ModuleVersion",
        "SharpClaw.ModuleHostKind",
        "SharpClaw.ModuleBootId",
    ];

    public static DurableRecordWrite Normalize(
        LogEvent logEvent,
        int maxRecordBytes)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (maxRecordBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));

        var message = SharpClawLogRedactor.Redact(logEvent.RenderMessage());
        var normalizedMessage = SharpClawLogBounds.TruncateUtf8(
            message,
            SharpClawLogBounds.MessageBytes,
            out var originalMessageBytes);
        var originalExceptionBytes = 0;
        string? exceptionText = null;
        if (logEvent.Exception is not null)
        {
            exceptionText = SharpClawLogBounds.TruncateUtf8(
                SharpClawLogRedactor.Redact(logEvent.Exception.ToString()),
                SharpClawLogBounds.ExceptionBytes,
                out originalExceptionBytes);
        }
        var template = SharpClawLogBounds.TruncateUtf8(
            SharpClawLogRedactor.Redact(logEvent.MessageTemplate.Text),
            SharpClawLogBounds.TemplateBytes,
            out var originalTemplateBytes);
        var category = Bound(
            GetString(logEvent, "SourceContext"),
            SharpClawLogBounds.CategoryBytes);
        var eventId = GetEventId(logEvent);
        var eventName = Bound(
            GetString(logEvent, "EventName") ?? eventId.Name ?? "Log",
            SharpClawLogBounds.EventNameBytes) ?? "Log";
        var eventIdName = Bound(
            eventId.Name,
            SharpClawLogBounds.EventIdNameBytes);
        var eventIdId = eventId.Id;
        var correlationId = Bound(
            GetString(logEvent, "CorrelationId")
                ?? GetString(logEvent, "RequestId"),
            SharpClawLogBounds.CorrelationIdBytes);
        var traceId = Bound(
            GetString(logEvent, "TraceId"),
            SharpClawLogBounds.TraceIdBytes);
        var spanId = Bound(
            GetString(logEvent, "SpanId"),
            SharpClawLogBounds.SpanIdBytes);
        var exceptionType = Bound(
            logEvent.Exception?.GetType().FullName,
            SharpClawLogBounds.ExceptionTypeBytes);
        var properties = CollectProperties(logEvent);
        var ownership = SharpClawLogOwnership.Current;

        if (ownership is not null)
        {
            AddProperty(properties, "SharpClaw.ModuleId", ownership.ModuleId, trusted: true);
            AddProperty(
                properties,
                "SharpClaw.ModuleVersion",
                ownership.ModuleVersion ?? "unknown",
                trusted: true);
            AddProperty(
                properties,
                "SharpClaw.ModuleHostKind",
                ownership.HostKind.ToString(),
                trusted: true);
            AddProperty(
                properties,
                "SharpClaw.ModuleBootId",
                ownership.BootId.ToString("D"),
                trusted: true);
        }

        if (originalMessageBytes > SharpClawLogBounds.MessageBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.Message", originalMessageBytes.ToString(CultureInfo.InvariantCulture));
        if (originalTemplateBytes > SharpClawLogBounds.TemplateBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.MessageTemplate", originalTemplateBytes.ToString(CultureInfo.InvariantCulture));
        if (originalExceptionBytes > SharpClawLogBounds.ExceptionBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.Exception", originalExceptionBytes.ToString(CultureInfo.InvariantCulture));
        if (logEvent.Exception is not null)
            AddProperty(properties, "SharpClaw.ExceptionPresent", "true");

        var record = new DurableRecordWrite(
            Guid.NewGuid(),
            logEvent.Timestamp,
            Bound(
                NormalizeLevel(logEvent.Level),
                SharpClawLogBounds.LevelBytes) ?? "I",
            eventName,
            normalizedMessage,
            exceptionType,
            correlationId,
            ExceptionText: exceptionText,
            MessageTemplate: template,
            Category: category,
            EventIdId: eventIdId,
            EventIdName: eventIdName,
            TraceId: traceId,
            SpanId: spanId,
            Properties: properties);

        return FitEncodedBody(record, maxRecordBytes);
    }

    private static Dictionary<string, string> CollectProperties(LogEvent logEvent)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in logEvent.Properties)
        {
            if (name is "SourceContext" or "EventId" or "EventName" or "CorrelationId"
                or "RequestId" or "TraceId" or "SpanId"
                || IsTrustedPropertyName(name)
                || SharpClawLogRedactor.IsSecretPropertyName(name))
            {
                continue;
            }

            var boundedName = SharpClawLogBounds.TruncateUtf8(
                name,
                SharpClawLogBounds.PropertyNameBytes,
                out _);
            var boundedValue = SharpClawLogBounds.TruncateUtf8(
                SharpClawLogRedactor.Redact(value.ToString()),
                SharpClawLogBounds.PropertyValueBytes,
                out _);
            AddProperty(result, boundedName, boundedValue);
            if (result.Count >= SharpClawLogBounds.UserPropertyCount)
                break;
        }

        return result;
    }

    private static DurableRecordWrite FitEncodedBody(
        DurableRecordWrite record,
        int maxRecordBytes)
    {
        var properties = new Dictionary<string, string>(
            record.Properties
                ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        record = record with { Properties = properties };

        while (DurableSegmentStore.MeasureEncodedRecordBody(record) > maxRecordBytes
               && RemoveLastUserProperty(properties))
        {
        }

        if (DurableSegmentStore.MeasureEncodedRecordBody(record) <= maxRecordBytes)
            return record;

        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.Message,
            static (current, value) => current with { Message = value ?? string.Empty },
            keepNonNull: true);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.ExceptionText,
            static (current, value) => current with { ExceptionText = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.MessageTemplate,
            static (current, value) => current with { MessageTemplate = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.Category,
            static (current, value) => current with { Category = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.CorrelationId,
            static (current, value) => current with { CorrelationId = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.EventIdName,
            static (current, value) => current with { EventIdName = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.TraceId,
            static (current, value) => current with { TraceId = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.SpanId,
            static (current, value) => current with { SpanId = value },
            keepNonNull: false);
        record = ShrinkField(
            record,
            maxRecordBytes,
            static current => current.EventName,
            static (current, value) => current with { EventName = value ?? "Log" },
            keepNonNull: true);

        foreach (var propertyName in TrustedPropertyNames)
        {
            record = ShrinkTrustedProperty(
                record,
                propertyName,
                maxRecordBytes);
        }

        if (DurableSegmentStore.MeasureEncodedRecordBody(record) > maxRecordBytes)
        {
            record = record with
            {
                Level = "I",
                EventName = "Log",
                Message = string.Empty,
                ExceptionType = null,
                CorrelationId = null,
                ExceptionText = null,
                MessageTemplate = string.Empty,
                Category = null,
                EventIdName = null,
                TraceId = null,
                SpanId = null,
            };
        }

        return record;
    }

    private static DurableRecordWrite ShrinkTrustedProperty(
        DurableRecordWrite record,
        string propertyName,
        int maxRecordBytes) =>
        ShrinkField(
            record,
            maxRecordBytes,
            current => current.Properties is not null
                && current.Properties.TryGetValue(propertyName, out var value)
                ? value
                : null,
            (current, value) =>
            {
                var properties = new Dictionary<string, string>(
                    current.Properties
                        ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    StringComparer.Ordinal);
                if (value is not null)
                    properties[propertyName] = value;
                return current with { Properties = properties };
            },
            keepNonNull: true);

    private static DurableRecordWrite ShrinkField(
        DurableRecordWrite record,
        int maxRecordBytes,
        Func<DurableRecordWrite, string?> getValue,
        Func<DurableRecordWrite, string?, DurableRecordWrite> setValue,
        bool keepNonNull)
    {
        var current = getValue(record);
        if (string.IsNullOrEmpty(current))
            return record;

        var low = 0;
        var high = Encoding.UTF8.GetByteCount(current) - 1;
        string? best = keepNonNull ? string.Empty : null;
        while (low <= high)
        {
            var candidateBytes = low + ((high - low) / 2);
            var candidate = SharpClawLogBounds.TruncateUtf8(
                current,
                candidateBytes,
                out _);
            var candidateRecord = setValue(record, candidate);
            if (DurableSegmentStore.MeasureEncodedRecordBody(candidateRecord)
                <= maxRecordBytes)
            {
                best = candidate;
                low = candidateBytes + 1;
            }
            else
            {
                high = candidateBytes - 1;
            }
        }

        return setValue(record, best);
    }

    private static bool RemoveLastUserProperty(
        Dictionary<string, string> properties)
    {
        foreach (var name in properties.Keys.Reverse())
        {
            if (IsTrustedPropertyName(name))
                continue;
            properties.Remove(name);
            return true;
        }

        return false;
    }

    private static bool IsTrustedPropertyName(string name) =>
        TrustedPropertyNames.Contains(name, StringComparer.Ordinal);

    private static string? Bound(string? value, int maximumBytes) =>
        value is null
            ? null
            : SharpClawLogBounds.TruncateUtf8(
                SharpClawLogRedactor.Redact(value),
                maximumBytes,
                out _);

    private static void AddProperty(
        Dictionary<string, string> properties,
        string name,
        string value,
        bool trusted = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var boundedValue = SharpClawLogBounds.TruncateUtf8(
            SharpClawLogRedactor.Redact(value),
            SharpClawLogBounds.PropertyValueBytes,
            out _);
        if (!trusted
            && properties.Count >= SharpClawLogBounds.UserPropertyCount
            && !properties.ContainsKey(name))
        {
            return;
        }

        properties[name] = boundedValue;
    }

    private static string? GetString(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            ? value is ScalarValue { Value: not null } scalar
                ? scalar.Value.ToString()
                : null
            : null;

    private static int? GetInt(LogEvent logEvent, string name)
    {
        var value = GetString(logEvent, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static (int? Id, string? Name) GetEventId(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("EventId", out var value))
            return (null, null);
        if (value is StructureValue structure)
        {
            int? id = null;
            string? name = null;
            foreach (var property in structure.Properties)
            {
                if (property.Name.Equals("Id", StringComparison.Ordinal))
                    id = int.TryParse(property.Value.ToString(), out var parsed) ? parsed : null;
                else if (property.Name.Equals("Name", StringComparison.Ordinal)
                         && property.Value is ScalarValue { Value: not null } scalar)
                    name = scalar.Value.ToString();
            }

            return (id, name);
        }

        return (GetInt(logEvent, "EventId"), null);
    }

    private static string NormalizeLevel(LogEventLevel level) => level.ToString();
}

public sealed class SharpClawLogDispatcher : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan CapacityWait = TimeSpan.FromSeconds(2);

    private readonly DurableSegmentStore _records;
    private readonly SharpClawLoggingOptions _options;
    private readonly DurableStreamKey _processStream;
    private readonly Channel<DispatchItem> _channel;
    private readonly CancellationTokenSource _timerCancellation = new();
    private readonly ConcurrentDictionary<DurableStreamKey, long> _drops = new();
    private readonly ConcurrentDictionary<DurableStreamKey, byte> _knownStreams = new();
    private readonly object _consoleGate = new();
    private readonly Task _worker;
    private readonly Task _timer;
    private int _shutdown;
    private long _droppedRecords;
    private string? _failure;

    public SharpClawLogDispatcher(
        DurableSegmentStore records,
        string appName,
        Guid bootId,
        SharpClawLoggingOptions options)
    {
        _records = records;
        _options = options;
        _processStream = DurableStreamKey.Process(appName, bootId);
        _knownStreams.TryAdd(_processStream, 0);
        _channel = Channel.CreateBounded<DispatchItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(ProcessLoopAsync);
        _timer = Task.Run(FlushLoopAsync);
    }

    public int QueueDepth => _channel.Reader.Count;
    public long DroppedRecords => Volatile.Read(ref _droppedRecords);
    public string? Failure => Volatile.Read(ref _failure);

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _shutdown) != 0)
            return;

        try
        {
            var record = SharpClawLogNormalizer.Normalize(
                logEvent,
                _records.MaxRecordBytes);
            var ownership = SharpClawLogOwnership.Current;
            var stream = ownership is null
                ? _processStream
                : DurableStreamKey.Module(ownership.ModuleId, ownership.BootId);
            _knownStreams.TryAdd(stream, 0);
            var item = new DispatchItem(
                stream,
                record,
                logEvent.Level,
                Completion: null);
            if (!TryEnqueue(item))
            {
                Interlocked.Increment(ref _droppedRecords);
                _drops.AddOrUpdate(stream, 1, static (_, count) => count + 1);
            }
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shutdown) != 0)
            return;

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(
                new DispatchItem(
                    _processStream,
                    null,
                    LogEventLevel.Information,
                    completion),
                cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAndSealAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            await Task.WhenAll(_worker, _timer)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _timerCancellation.Cancel();
        _channel.Writer.TryComplete();
        await Task.WhenAll(_timer, _worker)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.CompareExchange(ref _failure, exception.Message, null);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAndSealAsync().ConfigureAwait(false);
        _timerCancellation.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private bool TryEnqueue(DispatchItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;
        if (item.Record is null || item.Level < LogEventLevel.Warning)
            return false;

        try
        {
            if (!_channel.Writer.WaitToWriteAsync(_timerCancellation.Token)
                    .AsTask()
                    .WaitAsync(CapacityWait)
                    .GetAwaiter()
                    .GetResult())
            {
                return false;
            }

            return _channel.Writer.TryWrite(item);
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Record is null)
            {
                try
                {
                    await FlushKnownStreamsAsync().ConfigureAwait(false);
                    item.Completion?.TrySetResult();
                }
                catch (Exception ex)
                {
                    RecordFailure(ex);
                    item.Completion?.TrySetException(ex);
                }

                continue;
            }

            try
            {
                if (_drops.TryRemove(item.Stream, out var dropped) && dropped > 0)
                {
                    await _records.AppendAsync(
                            item.Stream,
                            new DurableRecordWrite(
                                Guid.NewGuid(),
                                DateTimeOffset.UtcNow,
                                "Warning",
                                "RecordsDropped",
                                $"Dropped {dropped} operational log record(s) because the bounded dispatcher was full.",
                                Properties: new Dictionary<string, string>
                                {
                                    ["DroppedCount"] = dropped.ToString(CultureInfo.InvariantCulture),
                                }),
                            DurableWriteMode.Durable)
                        .ConfigureAwait(false);
                }

                var mode = item.Level >= LogEventLevel.Error
                    ? DurableWriteMode.Durable
                    : DurableWriteMode.Buffered;
                await _records.AppendAsync(item.Stream, item.Record, mode)
                    .ConfigureAwait(false);
                RenderConsole(item.Record);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }

        try
        {
            await FlushKnownStreamsAsync().ConfigureAwait(false);
            foreach (var stream in _knownStreams.Keys)
                await _records.SealAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.FlushInterval);
            while (await timer.WaitForNextTickAsync(_timerCancellation.Token).ConfigureAwait(false))
            {
                _channel.Writer.TryWrite(new DispatchItem(
                    _processStream,
                    null,
                    LogEventLevel.Information,
                    Completion: null));
            }
        }
        catch (OperationCanceledException) when (_timerCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private async Task FlushKnownStreamsAsync()
    {
        foreach (var stream in _knownStreams.Keys)
            await _records.FlushAsync(stream).ConfigureAwait(false);
    }

    private void RenderConsole(DurableRecordWrite record)
    {
        if (!_options.ConsoleEnabled)
            return;

        var output = $"[{record.Timestamp:O}] {record.Level} {record.Category ?? record.EventName}: {record.Message}";
        if (!string.IsNullOrWhiteSpace(record.ExceptionText))
            output += Environment.NewLine + record.ExceptionText;

        lock (_consoleGate)
        {
            if (Enum.TryParse<LogEventLevel>(record.Level, out var level)
                && level >= LogEventLevel.Error)
            {
                Console.Error.WriteLine(output);
            }
            else
            {
                Console.WriteLine(output);
            }
        }
    }

    private sealed record DispatchItem(
        DurableStreamKey Stream,
        DurableRecordWrite? Record,
        LogEventLevel Level,
        TaskCompletionSource? Completion);
}

public sealed class SharpClawLogSink(SharpClawLogDispatcher dispatcher) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => dispatcher.Emit(logEvent);
}

public sealed class SharpClawLogRuntime : IAsyncDisposable, IDisposable
{
    private readonly bool _ownsStore;
    private readonly DurableSegmentStore _records;
    private readonly Serilog.ILogger _serilogLogger;
    private int _disposed;

    private SharpClawLogRuntime(
        string appName,
        Guid bootId,
        DurableSegmentStore records,
        SharpClawLogDispatcher dispatcher,
        Serilog.ILogger serilogLogger,
        bool ownsStore)
    {
        AppName = appName;
        BootId = bootId;
        _records = records;
        Dispatcher = dispatcher;
        _serilogLogger = serilogLogger;
        _ownsStore = ownsStore;
    }

    public string AppName { get; }
    public Guid BootId { get; }
    public DurableStreamKey ProcessStream => DurableStreamKey.Process(AppName, BootId);
    public SharpClawLogDispatcher Dispatcher { get; }
    public Serilog.ILogger SerilogLogger => _serilogLogger;

    public Task<DurableOperationalStreamCatalog> EnumerateOperationalStreamsAsync(
        DurableOperationalStreamEnumerationOptions options,
        CancellationToken cancellationToken = default) =>
        _records.EnumerateOperationalStreamsAsync(options, cancellationToken);

    public static SharpClawLogRuntime Create(
        string appName,
        DurableSegmentStore records,
        SharpClawLoggingOptions options,
        Guid? bootId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);

        return CreateCore(appName, records, options, bootId, ownsStore: false);
    }

    public static SharpClawLogRuntime Create(
        string appName,
        SharpClawInstancePaths paths,
        SharpClawLoggingOptions options,
        Guid? bootId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        var rootKey = EncryptionKeyResolver.ResolveKey(paths)
            ?? throw new InvalidOperationException("SharpClaw instance encryption key is unavailable.");
        var records = new DurableSegmentStore(new DurableStorageOptions
        {
            RootDirectory = paths.DurableDirectory,
            EncryptionKey = DurableStorageKeyDerivation.Derive(rootKey, "records"),
            AcquireWriterLease = false,
        });
        return CreateCore(appName, records, options, bootId, ownsStore: true);
    }

    private static SharpClawLogRuntime CreateCore(
        string appName,
        DurableSegmentStore records,
        SharpClawLoggingOptions options,
        Guid? bootId,
        bool ownsStore)
    {
        var resolvedBootId = bootId ?? Guid.NewGuid();
        var dispatcher = new SharpClawLogDispatcher(
            records,
            appName,
            resolvedBootId,
            options);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(options.MinimumLevel)
            .MinimumLevel.Override("Microsoft", options.MicrosoftMinimumLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", options.AspNetCoreMinimumLevel)
            .MinimumLevel.Override(
                "Microsoft.EntityFrameworkCore",
                options.EntityFrameworkCoreMinimumLevel)
            .MinimumLevel.Override("Uno", options.UnoMinimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new SharpClawLogSink(dispatcher))
            .CreateLogger();
        return new SharpClawLogRuntime(
            appName,
            resolvedBootId,
            records,
            dispatcher,
            logger,
            ownsStore);
    }

    public Task FlushAndSealAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.FlushAndSealAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await Dispatcher.DisposeAsync().ConfigureAwait(false);
        if (_serilogLogger is IDisposable disposable)
            disposable.Dispose();
        if (_ownsStore)
            await _records.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
