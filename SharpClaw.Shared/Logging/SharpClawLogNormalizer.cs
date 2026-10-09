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


internal static class SharpClawLogNormalizer
{
    private static readonly string[] TrustedPropertyNames =
    [
        "SharpClaw.SourceId",
        "SharpClaw.RegistrationVersion",
        "SharpClaw.RegistrationHostKind",
        "SharpClaw.RegistrationBootId",
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
            AddProperty(properties, "SharpClaw.SourceId", ownership.SourceId, trusted: true);
            AddProperty(
                properties,
                "SharpClaw.RegistrationVersion",
                ownership.RegistrationVersion ?? "unknown",
                trusted: true);
            AddProperty(
                properties,
                "SharpClaw.RegistrationHostKind",
                ownership.HostKind.ToString(),
                trusted: true);
            AddProperty(
                properties,
                "SharpClaw.RegistrationBootId",
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
