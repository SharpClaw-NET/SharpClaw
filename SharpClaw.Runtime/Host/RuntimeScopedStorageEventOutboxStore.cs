using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.Host;

/// <summary>
/// Stores Runtime event delivery records in the existing registration storage
/// collection, so this boundary needs no new database table or migration.
/// </summary>
public sealed class RuntimeScopedStorageEventOutboxStore(SharpClawDbContext db)
    : IRuntimeEventOutboxStore
{
    private const string SourceId = RuntimeEventDefinitions.SourceId;
    private const string StorageName = "event.outbox";
    private const string StateIndexName = "state";
    private const string Pending = "pending";
    private const string Failed = "failed";
    private const string Acknowledged = "acknowledged";
    private const string Cancelled = "cancelled";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask EnqueueAsync(
        RuntimeEventOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var recordKey = CreateRecordKey(message.EventId, message.TargetListenerId);
        var existing = await FindAsync(recordKey, cancellationToken);
        if (existing is not null)
            return;

        var now = DateTimeOffset.UtcNow;
        var document = new OutboxDocument(
            message.EventId,
            message.EventKey.Value,
            JsonSerializer.Serialize(
                message.Envelope,
                message.Envelope.GetType(),
                JsonOptions),
            message.Delivery,
            message.TargetListenerId,
            Pending,
            0,
            null,
            now,
            now);
        db.ScopedStorageRecords.Add(new ScopedStorageRecordDB
        {
            Id = Guid.NewGuid(),
            SourceId = SourceId,
            StorageName = StorageName,
            RecordKey = recordKey,
            ValueJson = JsonSerializer.Serialize(document, JsonOptions),
        });
        db.ScopedStorageIndexEntries.Add(new ScopedStorageIndexEntryDB
        {
            Id = Guid.NewGuid(),
            SourceId = SourceId,
            StorageName = StorageName,
            IndexName = StateIndexName,
            RecordKey = recordKey,
            StringValue = Pending,
        });
        await db.SaveChangesThroughKernelAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<RuntimeEventOutboxRecord>> ReadPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var keys = await db.ScopedStorageIndexEntries
            .AsNoTracking()
            .Where(index =>
                index.SourceId == SourceId &&
                index.StorageName == StorageName &&
                index.IndexName == StateIndexName &&
                (index.StringValue == Pending || index.StringValue == Failed))
            .OrderBy(index => index.CreatedAt)
            .ThenBy(index => index.RecordKey)
            .Take(limit)
            .Select(index => index.RecordKey)
            .ToArrayAsync(cancellationToken);

        var rows = await db.ScopedStorageRecords
            .AsNoTracking()
            .Where(record =>
                record.SourceId == SourceId &&
                record.StorageName == StorageName &&
                keys.Contains(record.RecordKey))
            .ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(row => row.RecordKey, StringComparer.Ordinal);
        return keys
            .Where(byKey.ContainsKey)
            .Select(key => Parse(byKey[key]))
            .ToArray();
    }

    public ValueTask AcknowledgeAsync(
        string recordKey,
        CancellationToken cancellationToken = default) =>
        SetStateAsync(recordKey, Acknowledged, null, cancellationToken);

    public ValueTask FailAsync(
        string recordKey,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return SetStateAsync(recordKey, Failed, error[..Math.Min(error.Length, 2_048)], cancellationToken);
    }

    public ValueTask CancelAsync(
        string recordKey,
        CancellationToken cancellationToken = default) =>
        SetStateAsync(recordKey, Cancelled, null, cancellationToken);

    private async ValueTask SetStateAsync(
        string recordKey,
        string state,
        string? error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        var row = await FindAsync(recordKey, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Runtime event outbox record '{recordKey}' was not found.");
        var current = Parse(row);
        var replacement = current with
        {
            State = state,
            Attempts = state == Failed ? current.Attempts + 1 : current.Attempts,
            LastError = error,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        row.ValueJson = JsonSerializer.Serialize(
            new OutboxDocument(
                replacement.EventId,
                replacement.EventKey.Value,
                replacement.EnvelopeJson,
                replacement.Delivery,
                replacement.TargetListenerId,
                replacement.State,
                replacement.Attempts,
                replacement.LastError,
                replacement.CreatedAt,
                replacement.UpdatedAt),
            JsonOptions);
        var stateIndex = await db.ScopedStorageIndexEntries.SingleOrDefaultAsync(
            index =>
                index.SourceId == SourceId &&
                index.StorageName == StorageName &&
                index.IndexName == StateIndexName &&
                index.RecordKey == recordKey,
            cancellationToken);
        if (stateIndex is null)
        {
            db.ScopedStorageIndexEntries.Add(new ScopedStorageIndexEntryDB
            {
                Id = Guid.NewGuid(),
                SourceId = SourceId,
                StorageName = StorageName,
                IndexName = StateIndexName,
                RecordKey = recordKey,
                StringValue = state,
            });
        }
        else
        {
            stateIndex.StringValue = state;
        }
        await db.SaveChangesThroughKernelAsync(cancellationToken);
    }

    private async ValueTask<ScopedStorageRecordDB?> FindAsync(
        string recordKey,
        CancellationToken cancellationToken) =>
        await db.ScopedStorageRecords.SingleOrDefaultAsync(
            record =>
                record.SourceId == SourceId &&
                record.StorageName == StorageName &&
                record.RecordKey == recordKey,
            cancellationToken);

    private static RuntimeEventOutboxRecord Parse(ScopedStorageRecordDB row)
    {
        var document = JsonSerializer.Deserialize<OutboxDocument>(row.ValueJson, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Runtime event outbox record '{row.RecordKey}' is empty.");
        if (!string.Equals(document.EventKey, "runtime.event", StringComparison.Ordinal) &&
            !document.EventKey.StartsWith("action.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime event outbox record '{row.RecordKey}' has an invalid event key.");
        }

        return new RuntimeEventOutboxRecord(
            row.RecordKey,
            document.EventId,
            new SharpClawEventKey(document.EventKey),
            document.EnvelopeJson,
            document.Delivery,
            document.TargetListenerId,
            document.State,
            document.Attempts,
            document.LastError,
            document.CreatedAt,
            document.UpdatedAt);
    }

    private static string CreateRecordKey(Guid eventId, string listenerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerId);
        var key = $"{eventId:N}:{listenerId}";
        return key.Length <= 256
            ? key
            : throw new ArgumentException(
                "The Runtime event listener identity is too long.",
                nameof(listenerId));
    }

    private sealed record OutboxDocument(
        Guid EventId,
        string EventKey,
        string EnvelopeJson,
        EventDelivery Delivery,
        string TargetListenerId,
        string State,
        int Attempts,
        string? LastError,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
