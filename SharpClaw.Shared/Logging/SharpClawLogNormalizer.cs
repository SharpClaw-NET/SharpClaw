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

    public static DurableRecordWrite Normalize(LogEvent logEvent, int maxRecordBytes)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordBytes, 1024);
        var text = NormalizeText(logEvent);
        var properties = CreateRecordProperties(logEvent, text);
        return FitEncodedBody(CreateRecord(logEvent, text, properties), maxRecordBytes);
    }

    private static NormalizedText NormalizeText(LogEvent logEvent)
    {
        var message = SharpClawLogBounds.TruncateUtf8(
            SharpClawLogRedactor.Redact(logEvent.RenderMessage(CultureInfo.InvariantCulture)),
            SharpClawLogBounds.MessageBytes, out var originalMessageBytes);
        var template = SharpClawLogBounds.TruncateUtf8(
            SharpClawLogRedactor.Redact(logEvent.MessageTemplate.Text),
            SharpClawLogBounds.TemplateBytes, out var originalTemplateBytes);
        var originalExceptionBytes = 0;
        var exception = logEvent.Exception is null ? null : SharpClawLogBounds.TruncateUtf8(
            SharpClawLogRedactor.Redact(logEvent.Exception.ToString()),
            SharpClawLogBounds.ExceptionBytes, out originalExceptionBytes);
        return new NormalizedText(message, template, exception,
            originalMessageBytes, originalTemplateBytes, originalExceptionBytes);
    }

    private static Dictionary<string, string> CreateRecordProperties(LogEvent logEvent, NormalizedText text)
    {
        var properties = CollectProperties(logEvent);
        var ownership = SharpClawLogOwnership.Current;
        if (ownership is not null)
        {
            AddProperty(properties, "SharpClaw.SourceId", ownership.SourceId, trusted: true);
            AddProperty(properties, "SharpClaw.RegistrationVersion",
                ownership.RegistrationVersion ?? "unknown", trusted: true);
            AddProperty(properties, "SharpClaw.RegistrationHostKind",
                ownership.HostKind.ToString(), trusted: true);
            AddProperty(properties, "SharpClaw.RegistrationBootId",
                ownership.BootId.ToString("D"), trusted: true);
        }
        if (text.OriginalMessageBytes > SharpClawLogBounds.MessageBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.Message", text.OriginalMessageBytes.ToString(CultureInfo.InvariantCulture));
        if (text.OriginalTemplateBytes > SharpClawLogBounds.TemplateBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.MessageTemplate", text.OriginalTemplateBytes.ToString(CultureInfo.InvariantCulture));
        if (text.OriginalExceptionBytes > SharpClawLogBounds.ExceptionBytes)
            AddProperty(properties, "SharpClaw.OriginalBytes.Exception", text.OriginalExceptionBytes.ToString(CultureInfo.InvariantCulture));
        if (logEvent.Exception is not null)
            AddProperty(properties, "SharpClaw.ExceptionPresent", "true");
        return properties;
    }

    private static DurableRecordWrite CreateRecord(
        LogEvent logEvent, NormalizedText text, Dictionary<string, string> properties)
    {
        var eventId = GetEventId(logEvent);
        return new DurableRecordWrite(
            Guid.NewGuid(), logEvent.Timestamp,
            Bound(NormalizeLevel(logEvent.Level), SharpClawLogBounds.LevelBytes) ?? "I",
            Bound(GetString(logEvent, "EventName") ?? eventId.Name ?? "Log", SharpClawLogBounds.EventNameBytes) ?? "Log",
            text.Message,
            Bound(logEvent.Exception?.GetType().FullName, SharpClawLogBounds.ExceptionTypeBytes),
            Bound(GetString(logEvent, "CorrelationId") ?? GetString(logEvent, "RequestId"), SharpClawLogBounds.CorrelationIdBytes),
            ExceptionText: text.Exception, MessageTemplate: text.Template,
            Category: Bound(GetString(logEvent, "SourceContext"), SharpClawLogBounds.CategoryBytes),
            EventIdId: eventId.Id,
            EventIdName: Bound(eventId.Name, SharpClawLogBounds.EventIdNameBytes),
            TraceId: Bound(GetString(logEvent, "TraceId"), SharpClawLogBounds.TraceIdBytes),
            SpanId: Bound(GetString(logEvent, "SpanId"), SharpClawLogBounds.SpanIdBytes),
            Properties: properties);
    }

    private readonly record struct NormalizedText(
        string Message, string Template, string? Exception,
        int OriginalMessageBytes, int OriginalTemplateBytes, int OriginalExceptionBytes);

    private static Dictionary<string, string> CollectProperties(LogEvent logEvent)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in logEvent.Properties)
        {
            if (IsRecordProperty(name)
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
                SharpClawLogRedactor.Redact(value.ToString(null, CultureInfo.InvariantCulture)),
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

        foreach (var field in ShrinkableFields)
        {
            record = ShrinkField(record, maxRecordBytes,
                field.GetValue, field.SetValue, field.KeepNonNull);
        }

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

    private static readonly BodyField[] ShrinkableFields =
    [
        new(static r => r.Message, static (r, v) => r with { Message = v ?? string.Empty }, true),
        new(static r => r.ExceptionText, static (r, v) => r with { ExceptionText = v }, false),
        new(static r => r.MessageTemplate, static (r, v) => r with { MessageTemplate = v }, false),
        new(static r => r.Category, static (r, v) => r with { Category = v }, false),
        new(static r => r.CorrelationId, static (r, v) => r with { CorrelationId = v }, false),
        new(static r => r.EventIdName, static (r, v) => r with { EventIdName = v }, false),
        new(static r => r.TraceId, static (r, v) => r with { TraceId = v }, false),
        new(static r => r.SpanId, static (r, v) => r with { SpanId = v }, false),
        new(static r => r.EventName, static (r, v) => r with { EventName = v ?? "Log" }, true),
    ];

    private readonly record struct BodyField(
        Func<DurableRecordWrite, string?> GetValue,
        Func<DurableRecordWrite, string?, DurableRecordWrite> SetValue,
        bool KeepNonNull);

    private static bool IsRecordProperty(string name) =>
        string.Equals(name, "SourceContext", StringComparison.Ordinal)
        || string.Equals(name, "EventId", StringComparison.Ordinal)
        || string.Equals(name, "EventName", StringComparison.Ordinal)
        || string.Equals(name, "CorrelationId", StringComparison.Ordinal)
        || string.Equals(name, "RequestId", StringComparison.Ordinal)
        || string.Equals(name, "TraceId", StringComparison.Ordinal)
        || string.Equals(name, "SpanId", StringComparison.Ordinal);

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
        if (DurableSegmentStore.MeasureEncodedRecordBody(record) <= maxRecordBytes)
            return record;

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
                ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture)
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
                    id = int.TryParse(property.Value.ToString(null, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                else if (property.Name.Equals("Name", StringComparison.Ordinal)
                         && property.Value is ScalarValue { Value: not null } scalar)
                    name = Convert.ToString(scalar.Value, CultureInfo.InvariantCulture);
            }

            return (id, name);
        }

        return (GetInt(logEvent, "EventId"), null);
    }

    private static string NormalizeLevel(LogEventLevel level) => level.ToString();
}
