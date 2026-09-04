using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.Host;

public sealed class ScopedStorageGateway(
    SharpClawDbContext db,
    IStorageContractProvider contracts,
    IRuntimeTransactionActionRunnerAccessor transactionRunnerAccessor,
    IStorageTelemetry? telemetry = null) : IScopedStorageGateway
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly ConcurrentDictionary<string, ScopedStorageMutationAndOutboxResult> CommitResults = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ScopedStorageClaimAuthority> Claims = new(StringComparer.Ordinal);

    public IReadOnlyList<ScopedStorageContractDescriptor> ListContracts() =>
        contracts.GetStorageContracts();

    public async Task<ScopedStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
        string SourceId,
        string storageName,
        ScopedStorageMutationAndOutboxRequest request,
        CancellationToken ct = default)
    {
        SourceId = RequireIdentifier(SourceId, nameof(SourceId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        ArgumentNullException.ThrowIfNull(request);
        var contract = RequireContract(SourceId, storageName);
        RequireOperation(contract, ScopedStorageOperations.MutateAndOutbox);
        if (request.Outbox.Count != 0)
        {
            throw new NotSupportedException(
                "The current host storage schema has no durable registration outbox.");
        }

        var commitKey = CommitKey(SourceId, storageName, request.Commit.IdempotencyKey);
        if (CommitResults.TryGetValue(commitKey, out var previous))
            return previous with { AlreadyCommitted = true };

        if (request.Mutations.Count == 0 || request.Mutations.Count > contract.MaxBatchSize)
            throw new ArgumentException("The atomic registration storage commit has an invalid mutation count.", nameof(request));

        var transactionRunner = transactionRunnerAccessor.GetRequiredRunner();
        await using var transaction = await transactionRunner.BeginSerializableAsync(ct);
        try
        {
            var pending = new List<PendingMutation>(request.Mutations.Count);
            foreach (var mutation in request.Mutations)
            {
                var key = RequireIdentifier(mutation.Key, nameof(mutation.Key), 256);
                if (mutation.Operation is not (ScopedStorageOperations.Upsert or ScopedStorageOperations.Delete))
                    throw new NotSupportedException($"Atomic registration storage operation '{mutation.Operation}' is not supported.");

                var record = await Records(contract)
                    .SingleOrDefaultAsync(candidate => candidate.RecordKey == key, ct);
                var actualRevision = record is null ? 0 : Revision(record);
                if (mutation.ExpectedRevision is { } expected && expected != actualRevision)
                    throw RevisionConflict(key, expected, actualRevision);
                ValidateAuthority(SourceId, storageName, key, mutation.Authority, actualRevision);

                IReadOnlyList<ScopedStorageIndexEntryDB> indexes = [];
                string? valueJson = null;
                if (mutation.Operation == ScopedStorageOperations.Upsert)
                {
                    if (mutation.Value is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        throw new ArgumentException("Atomic registration storage upsert requires a value.", nameof(request));
                    ValidateDocumentSize(contract, value);
                    indexes = mutation.Indexes is null
                        ? []
                        : ReadIndexes(
                            contract,
                            key,
                            mutation.Indexes is JsonElement element
                                ? element
                                : JsonSerializer.SerializeToElement(mutation.Indexes, JsonOptions));
                    valueJson = value.GetRawText();
                }

                pending.Add(new PendingMutation(
                    mutation,
                    key,
                    record,
                    actualRevision,
                    valueJson,
                    indexes));
            }

            var writtenRecords = new Dictionary<string, ScopedStorageRecordDB>(StringComparer.Ordinal);
            foreach (var item in pending)
            {
                if (item.Mutation.Operation == ScopedStorageOperations.Delete)
                {
                    if (item.Record is not null)
                        db.ScopedStorageRecords.Remove(item.Record);
                    await DeleteIndexesAsync(contract, item.Key, ct);
                    continue;
                }

                var record = item.Record ?? new ScopedStorageRecordDB
                {
                    Id = Guid.NewGuid(),
                    SourceId = contract.SourceId,
                    StorageName = contract.StorageName,
                    RecordKey = item.Key,
                    ValueJson = item.ValueJson!,
                };
                if (item.Record is null)
                    db.ScopedStorageRecords.Add(record);
                else
                    record.ValueJson = item.ValueJson!;
                writtenRecords[item.Key] = record;

                await DeleteIndexesAsync(contract, item.Key, ct);
                db.ScopedStorageIndexEntries.AddRange(item.Indexes);
            }

            await db.SaveChangesThroughKernelAsync(ct);
            var revisions = pending
                .Select(item => new ScopedStorageRevision(
                    item.Key,
                    item.Mutation.Operation == ScopedStorageOperations.Delete
                        ? item.ActualRevision + 1
                        : Revision(writtenRecords[item.Key])))
                .ToArray();

            if (transaction is not null)
                await transactionRunner.CommitAsync(transaction, ct);

            var result = new ScopedStorageMutationAndOutboxResult(
                request.Commit,
                revisions,
                [],
                revisions.Max(revision => revision.Revision));
            CommitResults.TryAdd(commitKey, result);
            foreach (var item in pending)
                AdvanceClaim(SourceId, storageName, item.Key, item.Mutation.Authority, revisions.First(value => value.Key == item.Key).Revision);
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transactionRunner.RollbackAsync(transaction, CancellationToken.None);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    public async Task<ScopedStorageClaimResult<T>> ClaimAsync<T>(
        string SourceId,
        string storageName,
        ScopedStorageClaimRequest request,
        CancellationToken ct = default)
    {
        SourceId = RequireIdentifier(SourceId, nameof(SourceId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.OwnerId, SourceId, StringComparison.Ordinal))
            throw ScopedStorageFailure(ScopedStorageErrors.ClaimOwnerMismatch, "The claim owner does not match the storage owner.");

        var contract = RequireContract(SourceId, storageName);
        RequireOperation(contract, ScopedStorageOperations.Claim);
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            filters = request.Filters,
            orderBy = request.OrderBy,
            limit = request.Limit,
            patch = request.Patch,
            indexes = request.Indexes,
        }, JsonOptions));
        var claim = ReadClaim(contract, parameters.RootElement);
        var transactionRunner = transactionRunnerAccessor.GetRequiredRunner();
        await using var transaction = await transactionRunner.BeginSerializableAsync(ct);
        try
        {
            var records = await LoadQueryRecordsAsync(contract, claim.Query, tracking: true, ct);
            if (records.Count == 0)
            {
                if (transaction is not null)
                    await transactionRunner.CommitAsync(transaction, ct);
                return new ScopedStorageClaimResult<T>(
                    [],
                    NewClaimAuthority(SourceId, storageName, null, 0, request.Authority));
            }

            var now = DateTimeOffset.UtcNow;
            var generation = 1L;
            foreach (var record in records)
            {
                var claimKey = ClaimKey(SourceId, storageName, record.RecordKey);
                if (Claims.TryGetValue(claimKey, out var existing))
                {
                    if (request.Authority is null || !existing.Matches(request.Authority) || !existing.IsValidAt(now))
                        throw ScopedStorageFailure(ScopedStorageErrors.StaleClaim, "The storage record already has a live claim.", record.RecordKey);
                    generation = Math.Max(generation, existing.Generation + 1);
                }

                var actualRevision = Revision(record);
                if (request.ExpectedRevision is { } expected && expected != actualRevision)
                    throw RevisionConflict(record.RecordKey, expected, actualRevision);
                Claims.TryGetValue(claimKey, out var authorityClaim);
                if (request.Authority is not null &&
                    (authorityClaim is null || !authorityClaim.Matches(request.Authority)))
                    throw ScopedStorageFailure(ScopedStorageErrors.ClaimAuthorityMismatch, "The requested claim authority is not active.", record.RecordKey);
            }

            ValidateClaimPatchIndexedFields(contract, claim.Patch, claim.IndexUpdates);
            foreach (var record in records)
                record.ValueJson = ApplyPatch(record.ValueJson, claim.Patch);
            await ReplaceClaimIndexesAsync(
                contract,
                records.Select(record => record.RecordKey).ToArray(),
                claim.IndexUpdates,
                ct);
            await db.SaveChangesThroughKernelAsync(ct);

            var authority = NewClaimAuthority(
                SourceId,
                storageName,
                records,
                generation,
                request.Authority);
            var resultRecordsList = new List<ScopedStorageClaimRecord<T>>(records.Count);
            foreach (var record in records)
            {
                var value = JsonSerializer.Deserialize<T>(record.ValueJson, JsonOptions)
                    ?? throw new InvalidOperationException("A claimed registration storage value could not be decoded.");
                var indexes = await Indexes(contract)
                    .Where(index => index.RecordKey == record.RecordKey)
                    .ToListAsync(ct);
                resultRecordsList.Add(new ScopedStorageClaimRecord<T>(
                    record.RecordKey,
                    value,
                    Revision(record),
                    authority,
                    IndexesResponse(indexes)));
            }
            var resultRecords = resultRecordsList.ToArray();
            authority = authority with
            {
                Revision = resultRecords.Max(record => record.Revision),
            };
            foreach (var record in resultRecords)
                Claims[ClaimKey(SourceId, storageName, record.Key)] = authority;

            if (transaction is not null)
                await transactionRunner.CommitAsync(transaction, ct);
            return new ScopedStorageClaimResult<T>(resultRecords, authority);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transactionRunner.RollbackAsync(transaction, CancellationToken.None);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    public async Task<ScopedStorageClaimRenewalResult> RenewClaimAsync(
        string SourceId,
        string storageName,
        ScopedStorageClaimRenewalRequest request,
        CancellationToken ct = default)
    {
        SourceId = RequireIdentifier(SourceId, nameof(SourceId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        ArgumentNullException.ThrowIfNull(request);
        var prefix = ClaimPrefix(SourceId, storageName);
        foreach (var pair in Claims.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var current = pair.Value;
            if (!string.Equals(current.OwnerId, request.OwnerId, StringComparison.Ordinal) ||
                current.HostToken != request.HostToken ||
                current.Generation != request.Generation ||
                !current.IsValidAt(DateTimeOffset.UtcNow))
                continue;

            var key = pair.Key[prefix.Length..];
            var contract = RequireContract(SourceId, storageName);
            var record = await Records(contract).SingleOrDefaultAsync(item => item.RecordKey == key, ct);
            if (record is null)
                break;
            var renewed = current with
            {
                LeaseExpiresAt = request.RequestedLeaseExpiresAt,
                Revision = Revision(record),
            };
            Claims[pair.Key] = renewed;
            return new ScopedStorageClaimRenewalResult(true, renewed);
        }

        return new ScopedStorageClaimRenewalResult(false, null, ScopedStorageErrors.StaleClaim);
    }

    public Task<ScopedStorageClaimRecoveryResult> RecoverClaimAsync(
        string SourceId,
        string storageName,
        ScopedStorageClaimRecoveryRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SourceId = RequireIdentifier(SourceId, nameof(SourceId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        ArgumentNullException.ThrowIfNull(request);
        var prefix = ClaimPrefix(SourceId, storageName);
        foreach (var pair in Claims.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var current = pair.Value;
            if (string.Equals(current.OwnerId, request.OwnerId, StringComparison.Ordinal) &&
                current.HostToken == request.HostToken &&
                current.Generation == request.Generation)
            {
                Claims.TryRemove(pair.Key, out _);
                return Task.FromResult(new ScopedStorageClaimRecoveryResult(true, current));
            }
        }

        return Task.FromResult(new ScopedStorageClaimRecoveryResult(false, null, ScopedStorageErrors.StaleClaim));
    }

    public async Task<JsonElement> InvokeAsync(
        string SourceId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default)
    {
        SourceId = RequireIdentifier(SourceId, nameof(SourceId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        operation = NormalizeOperation(RequireIdentifier(operation, nameof(operation), 64));

        var contract = RequireContract(SourceId, storageName);
        RequireOperation(contract, operation);

        var started = Stopwatch.GetTimestamp();
        var inputBytes = Encoding.UTF8.GetByteCount(parameters.GetRawText());
        var success = false;
        var recordCount = 0;
        long outputBytes = 0;

        try
        {
            var result = operation switch
            {
                ScopedStorageOperations.Get => await GetAsync(contract, parameters, ct),
                ScopedStorageOperations.Upsert => await UpsertAsync(contract, parameters, ct),
                ScopedStorageOperations.BatchUpsert => await BatchUpsertAsync(contract, parameters, ct),
                ScopedStorageOperations.Delete => await DeleteAsync(contract, parameters, ct),
                ScopedStorageOperations.BatchDelete => await BatchDeleteAsync(contract, parameters, ct),
                ScopedStorageOperations.List => await ListAsync(contract, parameters, ct),
                ScopedStorageOperations.Query => await QueryAsync(contract, parameters, ct),
                ScopedStorageOperations.Claim => await ClaimAsync(contract, parameters, ct),
                _ => throw new NotSupportedException(
                    $"Registration storage operation '{operation}' is not supported."),
            };

            outputBytes = Encoding.UTF8.GetByteCount(result.GetRawText());
            recordCount = CountRecords(result);
            success = true;
            return result;
        }
        finally
        {
            telemetry?.Record(new ScopedStorageTelemetryEvent(
                SourceId,
                storageName,
                operation,
                success,
                Stopwatch.GetElapsedTime(started),
                inputBytes,
                outputBytes,
                recordCount));
        }
    }

    private async Task<JsonElement> GetAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var key = ReadRequiredString(parameters, "key", 256);
        var record = await Records(contract)
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.RecordKey == key, ct);

        if (record is null)
            return JsonSerializer.SerializeToElement(new { found = false }, JsonOptions);

        using var value = JsonDocument.Parse(record.ValueJson);
        return JsonSerializer.SerializeToElement(new
        {
            found = true,
            key = record.RecordKey,
            value = value.RootElement,
            revision = Revision(record),
            indexes = await ReadIndexesAsync(contract, record.RecordKey, ct),
        }, JsonOptions);
    }

    private async Task<JsonElement> UpsertAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var write = ReadWrite(contract, parameters);
        await UpsertRecordAsync(contract, write, ct);
        await db.SaveChangesThroughKernelAsync(ct);
        return JsonSerializer.SerializeToElement(new { saved = true }, JsonOptions);
    }

    private async Task<JsonElement> BatchUpsertAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var writes = ReadWrites(contract, parameters);
        foreach (var write in writes)
            await UpsertRecordAsync(contract, write, ct);

        if (writes.Count > 0)
            await db.SaveChangesThroughKernelAsync(ct);

        return JsonSerializer.SerializeToElement(new { saved = writes.Count }, JsonOptions);
    }

    private async Task UpsertRecordAsync(
        ScopedStorageContractDescriptor contract,
        StorageWrite write,
        CancellationToken ct)
    {
        var record = await Records(contract)
            .SingleOrDefaultAsync(record => record.RecordKey == write.Key, ct);
        if (record is null)
        {
            record = new ScopedStorageRecordDB
            {
                Id = Guid.NewGuid(),
                SourceId = contract.SourceId,
                StorageName = contract.StorageName,
                RecordKey = write.Key,
                ValueJson = write.ValueJson,
            };
            db.ScopedStorageRecords.Add(record);
        }
        else
        {
            record.ValueJson = write.ValueJson;
        }

        await DeleteIndexesAsync(contract, write.Key, ct);
        db.ScopedStorageIndexEntries.AddRange(write.Indexes);
    }

    private async Task<JsonElement> DeleteAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var key = ReadRequiredString(parameters, "key", 256);
        var record = await Records(contract)
            .SingleOrDefaultAsync(record => record.RecordKey == key, ct);
        var deleted = record is not null;

        if (record is not null)
            db.ScopedStorageRecords.Remove(record);

        var removedIndexes = await DeleteIndexesAsync(contract, key, ct);
        if (deleted || removedIndexes)
            await db.SaveChangesThroughKernelAsync(ct);

        return JsonSerializer.SerializeToElement(new { deleted }, JsonOptions);
    }

    private async Task<JsonElement> BatchDeleteAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var keys = ReadKeys(contract, parameters);
        if (keys.Count == 0)
            return JsonSerializer.SerializeToElement(new { deleted = 0 }, JsonOptions);

        var records = await Records(contract)
            .Where(record => keys.Contains(record.RecordKey))
            .ToListAsync(ct);
        var indexes = await Indexes(contract)
            .Where(index => keys.Contains(index.RecordKey))
            .ToListAsync(ct);

        db.ScopedStorageRecords.RemoveRange(records);
        db.ScopedStorageIndexEntries.RemoveRange(indexes);
        await db.SaveChangesThroughKernelAsync(ct);

        return JsonSerializer.SerializeToElement(new { deleted = records.Count }, JsonOptions);
    }

    private async Task<JsonElement> ListAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var offset = ReadOptionalInt(parameters, "offset", 0, 100_000) ?? 0;
        var limit = ReadOptionalInt(parameters, "limit", 1, 1_000);
        IQueryable<ScopedStorageRecordDB> query = Records(contract)
            .AsNoTracking()
            .OrderBy(record => record.RecordKey)
            .Skip(offset);
        if (limit is { } take)
            query = query.Take(take);

        return RecordsResponse(await query.ToListAsync(ct));
    }

    private async Task<JsonElement> QueryAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var query = ReadQuery(contract, parameters);
        var records = await LoadQueryRecordsAsync(contract, query, tracking: false, ct);
        return RecordsResponse(records);
    }

    private async Task<JsonElement> ClaimAsync(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var claim = ReadClaim(contract, parameters);
        var transactionRunner = transactionRunnerAccessor.GetRequiredRunner();
        await using var transaction = await transactionRunner.BeginSerializableAsync(ct);
        try
        {
            var records = await LoadQueryRecordsAsync(contract, claim.Query, tracking: true, ct);
            if (records.Count == 0)
            {
                if (transaction is not null)
                    await transactionRunner.CommitAsync(transaction, ct);
                return RecordsResponse([]);
            }

            ValidateClaimPatchIndexedFields(contract, claim.Patch, claim.IndexUpdates);

            foreach (var record in records)
                record.ValueJson = ApplyPatch(record.ValueJson, claim.Patch);

            await ReplaceClaimIndexesAsync(
                contract,
                records.Select(record => record.RecordKey).ToArray(),
                claim.IndexUpdates,
                ct);

            await db.SaveChangesThroughKernelAsync(ct);
            if (transaction is not null)
                await transactionRunner.CommitAsync(transaction, ct);

            return RecordsResponse(records);
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                try
                {
                    await transactionRunner.RollbackAsync(transaction, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The registration storage transaction failed and rollback also failed.",
                        exception,
                        rollbackException);
                }
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private async Task<IReadOnlyList<ScopedStorageRecordDB>> LoadQueryRecordsAsync(
        ScopedStorageContractDescriptor contract,
        StorageQuery query,
        bool tracking,
        CancellationToken ct)
    {
        if (query.Filters.Count == 0 && query.OrderBy is null)
            throw new ArgumentException("Registration storage query requires at least one filter or order index.");

        var keys = await FindMatchingRecordKeysAsync(contract, query.Filters, ct);
        if (query.Filters.Count > 0 && keys.Count == 0)
            return [];

        var limit = query.Limit ?? 1_000;
        if (query.OrderBy is not null)
        {
            var orderedKeys = await LoadOrderedKeysAsync(contract, query.OrderBy, keys, limit, ct);
            return await LoadRecordsByKeysAsync(contract, orderedKeys, tracking, ct);
        }

        var unorderedKeys = keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return await LoadRecordsByKeysAsync(contract, unorderedKeys, tracking, ct);
    }

    private async Task<HashSet<string>> FindMatchingRecordKeysAsync(
        ScopedStorageContractDescriptor contract,
        IReadOnlyList<StorageFilter> filters,
        CancellationToken ct)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        var initialized = false;

        foreach (var filter in filters)
        {
            var descriptor = RequireIndex(contract, filter.IndexName);
            var value = ReadIndexValue(filter.Value, descriptor.ValueKind);
            var indexQuery = ApplyComparison(
                Indexes(contract).AsNoTracking().Where(index => index.IndexName == filter.IndexName),
                descriptor.ValueKind,
                filter.Operator,
                value);

            var keys = await indexQuery
                .Select(index => index.RecordKey)
                .Distinct()
                .ToListAsync(ct);

            if (!initialized)
            {
                matches.UnionWith(keys);
                initialized = true;
            }
            else
            {
                matches.IntersectWith(keys);
            }

            if (matches.Count == 0)
                break;
        }

        return matches;
    }

    private async Task<IReadOnlyList<string>> LoadOrderedKeysAsync(
        ScopedStorageContractDescriptor contract,
        StorageOrder order,
        HashSet<string> filteredKeys,
        int limit,
        CancellationToken ct)
    {
        var descriptor = RequireIndex(contract, order.IndexName);
        var descending = string.Equals(
            order.Direction,
            ScopedStorageSortDirections.Descending,
            StringComparison.Ordinal);
        var orderQuery = Indexes(contract)
            .AsNoTracking()
            .Where(index => index.IndexName == order.IndexName);

        if (filteredKeys.Count > 0)
        {
            var keys = filteredKeys.ToArray();
            orderQuery = orderQuery.Where(index => keys.Contains(index.RecordKey));
        }

        var orderedIndexes = await OrderIndexes(orderQuery, descriptor.ValueKind, descending)
            .Take(limit)
            .ToListAsync(ct);

        var orderedKeys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in orderedIndexes)
        {
            if (seen.Add(index.RecordKey))
                orderedKeys.Add(index.RecordKey);
        }

        return orderedKeys;
    }

    private async Task<IReadOnlyList<ScopedStorageRecordDB>> LoadRecordsByKeysAsync(
        ScopedStorageContractDescriptor contract,
        IReadOnlyList<string> keys,
        bool tracking,
        CancellationToken ct)
    {
        if (keys.Count == 0)
            return [];

        var keySet = keys.ToArray();
        IQueryable<ScopedStorageRecordDB> query = Records(contract)
            .Where(record => keySet.Contains(record.RecordKey));
        if (!tracking)
            query = query.AsNoTracking();

        var records = await query.ToListAsync(ct);
        var byKey = records.ToDictionary(record => record.RecordKey, StringComparer.Ordinal);
        var ordered = new List<ScopedStorageRecordDB>();
        foreach (var key in keys)
        {
            if (byKey.TryGetValue(key, out var record))
                ordered.Add(record);
        }

        return ordered;
    }

    private async Task ReplaceClaimIndexesAsync(
        ScopedStorageContractDescriptor contract,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, IReadOnlyList<IndexValue>> indexUpdates,
        CancellationToken ct)
    {
        if (indexUpdates.Count == 0)
            return;

        var indexNames = indexUpdates.Keys.ToArray();
        var existing = await Indexes(contract)
            .Where(index => keys.Contains(index.RecordKey) && indexNames.Contains(index.IndexName))
            .ToListAsync(ct);
        db.ScopedStorageIndexEntries.RemoveRange(existing);

        foreach (var key in keys)
        {
            foreach (var (indexName, values) in indexUpdates)
            {
                foreach (var value in values)
                    db.ScopedStorageIndexEntries.Add(CreateIndexEntry(contract, key, indexName, value));
            }
        }
    }

    private IQueryable<ScopedStorageRecordDB> Records(ScopedStorageContractDescriptor contract) =>
        db.ScopedStorageRecords.Where(record =>
            record.SourceId == contract.SourceId
            && record.StorageName == contract.StorageName);

    private IQueryable<ScopedStorageIndexEntryDB> Indexes(ScopedStorageContractDescriptor contract) =>
        db.ScopedStorageIndexEntries.Where(index =>
            index.SourceId == contract.SourceId
            && index.StorageName == contract.StorageName);

    private static IQueryable<ScopedStorageIndexEntryDB> ApplyComparison(
        IQueryable<ScopedStorageIndexEntryDB> query,
        ScopedStorageIndexValueKind valueKind,
        string comparisonOperator,
        IndexValue value) =>
        valueKind switch
        {
            ScopedStorageIndexValueKind.String => comparisonOperator switch
            {
                ScopedStorageComparisonOperators.EqualTo => query.Where(index => index.StringValue == value.StringValue),
                _ => throw new ArgumentException("String index values only support equality comparisons."),
            },
            ScopedStorageIndexValueKind.Number => comparisonOperator switch
            {
                ScopedStorageComparisonOperators.EqualTo => query.Where(index => index.NumberValue == value.NumberValue),
                ScopedStorageComparisonOperators.LessThanOrEqual => query.Where(index => index.NumberValue <= value.NumberValue),
                ScopedStorageComparisonOperators.GreaterThanOrEqual => query.Where(index => index.NumberValue >= value.NumberValue),
                _ => query,
            },
            ScopedStorageIndexValueKind.DateTime => comparisonOperator switch
            {
                ScopedStorageComparisonOperators.EqualTo => query.Where(index => index.DateTimeValue == value.DateTimeValue),
                ScopedStorageComparisonOperators.LessThanOrEqual => query.Where(index => index.DateTimeValue <= value.DateTimeValue),
                ScopedStorageComparisonOperators.GreaterThanOrEqual => query.Where(index => index.DateTimeValue >= value.DateTimeValue),
                _ => query,
            },
            ScopedStorageIndexValueKind.Bool => comparisonOperator switch
            {
                ScopedStorageComparisonOperators.EqualTo => query.Where(index => index.BoolValue == value.BoolValue),
                _ => throw new ArgumentException("Boolean index values only support equality comparisons."),
            },
            _ => query,
        };

    private static IOrderedQueryable<ScopedStorageIndexEntryDB> OrderIndexes(
        IQueryable<ScopedStorageIndexEntryDB> query,
        ScopedStorageIndexValueKind valueKind,
        bool descending) =>
        (valueKind, descending) switch
        {
            (ScopedStorageIndexValueKind.String, false) => query
                .OrderBy(index => index.StringValue)
                .ThenBy(index => index.RecordKey),
            (ScopedStorageIndexValueKind.String, true) => query
                .OrderByDescending(index => index.StringValue)
                .ThenByDescending(index => index.RecordKey),
            (ScopedStorageIndexValueKind.Number, false) => query
                .OrderBy(index => index.NumberValue)
                .ThenBy(index => index.RecordKey),
            (ScopedStorageIndexValueKind.Number, true) => query
                .OrderByDescending(index => index.NumberValue)
                .ThenByDescending(index => index.RecordKey),
            (ScopedStorageIndexValueKind.DateTime, false) => query
                .OrderBy(index => index.DateTimeValue)
                .ThenBy(index => index.RecordKey),
            (ScopedStorageIndexValueKind.DateTime, true) => query
                .OrderByDescending(index => index.DateTimeValue)
                .ThenByDescending(index => index.RecordKey),
            (ScopedStorageIndexValueKind.Bool, false) => query
                .OrderBy(index => index.BoolValue)
                .ThenBy(index => index.RecordKey),
            (ScopedStorageIndexValueKind.Bool, true) => query
                .OrderByDescending(index => index.BoolValue)
                .ThenByDescending(index => index.RecordKey),
            _ => query.OrderBy(index => index.RecordKey),
        };

    private async Task<bool> DeleteIndexesAsync(
        ScopedStorageContractDescriptor contract,
        string key,
        CancellationToken ct)
    {
        var indexes = await Indexes(contract)
            .Where(index => index.RecordKey == key)
            .ToListAsync(ct);
        db.ScopedStorageIndexEntries.RemoveRange(indexes);
        return indexes.Count > 0;
    }

    private StorageWrite ReadWrite(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var key = ReadRequiredString(parameters, "key", 256);
        if (!parameters.TryGetProperty("value", out var value)
            || value.ValueKind is JsonValueKind.Undefined)
        {
            throw new ArgumentException("Registration storage upsert requires a value.", nameof(parameters));
        }

        ValidateDocumentSize(contract, value);
        var indexes = parameters.TryGetProperty("indexes", out var indexElement)
            && indexElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? ReadIndexes(contract, key, indexElement)
                : [];

        return new StorageWrite(key, value.GetRawText(), indexes);
    }

    private List<StorageWrite> ReadWrites(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Registration storage batchUpsert requires a records array.", nameof(parameters));
        }

        var writes = records.EnumerateArray()
            .Select(record => ReadWrite(contract, record))
            .ToList();
        if (writes.Count > contract.MaxBatchSize)
        {
            throw new ArgumentException(
                $"Registration storage batchUpsert for '{contract.SourceId}/{contract.StorageName}' " +
                $"cannot exceed {contract.MaxBatchSize} records.",
                nameof(parameters));
        }

        return writes;
    }

    private static List<string> ReadKeys(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Registration storage batchDelete requires a keys array.", nameof(parameters));
        }

        var result = keys.EnumerateArray()
            .Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Registration storage batchDelete keys must be strings.", nameof(parameters));
                return RequireIdentifier(value.GetString() ?? "", "key", 256);
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (result.Count > contract.MaxBatchSize)
        {
            throw new ArgumentException(
                $"Registration storage batchDelete for '{contract.SourceId}/{contract.StorageName}' " +
                $"cannot exceed {contract.MaxBatchSize} keys.",
                nameof(parameters));
        }

        return result;
    }

    private StorageQuery ReadQuery(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var filters = ReadFilters(contract, parameters);
        var order = ReadOrder(contract, parameters);
        var limit = ReadOptionalInt(parameters, "limit", 1, 1_000);
        return new StorageQuery(filters, order, limit);
    }

    private StorageClaim ReadClaim(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var query = ReadQuery(contract, parameters);
        if (!parameters.TryGetProperty("patch", out var patch)
            || patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Registration storage claim requires an object patch.", nameof(parameters));
        }

        var indexes = parameters.TryGetProperty("indexes", out var indexElement)
            && indexElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? ReadIndexUpdates(contract, indexElement)
                : new Dictionary<string, IReadOnlyList<IndexValue>>(StringComparer.Ordinal);

        return new StorageClaim(query, patch.Clone(), indexes);
    }

    private static IReadOnlyList<StorageFilter> ReadFilters(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("filters", out var filtersElement)
            || filtersElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (filtersElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Registration storage query filters must be an array.", nameof(parameters));

        var filters = new List<StorageFilter>();
        foreach (var element in filtersElement.EnumerateArray())
        {
            var indexName = ReadRequiredString(element, "indexName", 128);
            var comparisonOperator = NormalizeComparisonOperator(
                ReadRequiredString(element, "operator", 64));
            if (!element.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new ArgumentException("Registration storage query filters require a value.", nameof(parameters));
            }

            var descriptor = RequireIndex(contract, indexName);
            RequireComparison(descriptor, comparisonOperator);
            _ = ReadIndexValue(value, descriptor.ValueKind);
            filters.Add(new StorageFilter(indexName, comparisonOperator, value.Clone()));
        }

        return filters;
    }

    private static StorageOrder? ReadOrder(
        ScopedStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("orderBy", out var orderElement)
            || orderElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (orderElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Registration storage orderBy must be an object.", nameof(parameters));

        var indexName = ReadRequiredString(orderElement, "indexName", 128);
        var direction = NormalizeSortDirection(
            ReadOptionalString(orderElement, "direction", 16)
            ?? ScopedStorageSortDirections.Ascending);
        _ = RequireIndex(contract, indexName);
        return new StorageOrder(indexName, direction);
    }

    private static IReadOnlyList<ScopedStorageIndexEntryDB> ReadIndexes(
        ScopedStorageContractDescriptor contract,
        string key,
        JsonElement indexes)
    {
        return ReadIndexUpdates(contract, indexes)
            .SelectMany(update => update.Value.Select(value =>
                CreateIndexEntry(contract, key, update.Key, value)))
            .ToList();
    }

    private static Dictionary<string, IReadOnlyList<IndexValue>> ReadIndexUpdates(
        ScopedStorageContractDescriptor contract,
        JsonElement indexes)
    {
        if (indexes.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Registration storage indexes must be a JSON object.", nameof(indexes));

        var result = new Dictionary<string, IReadOnlyList<IndexValue>>(StringComparer.Ordinal);
        foreach (var property in indexes.EnumerateObject())
        {
            var indexName = RequireIdentifier(property.Name, "indexName", 128);
            var descriptor = RequireIndex(contract, indexName);
            var values = ExpandIndexValues(property.Value)
                .Select(value => ReadIndexValue(value, descriptor.ValueKind))
                .ToArray();
            result[indexName] = values;
        }

        return result;
    }

    private static IEnumerable<JsonElement> ExpandIndexValues(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        if (value.ValueKind != JsonValueKind.Array)
            return [value];

        return value.EnumerateArray()
            .Where(item => item.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            .ToArray();
    }

    private static IndexValue ReadIndexValue(
        JsonElement value,
        ScopedStorageIndexValueKind expectedKind)
    {
        return expectedKind switch
        {
            ScopedStorageIndexValueKind.String when value.ValueKind == JsonValueKind.String =>
                new IndexValue(expectedKind, value.GetString() ?? "", null, null, null),
            ScopedStorageIndexValueKind.Number when value.ValueKind == JsonValueKind.Number
                                                 && value.TryGetDouble(out var number) =>
                new IndexValue(expectedKind, null, number, null, null),
            ScopedStorageIndexValueKind.DateTime when value.ValueKind == JsonValueKind.String
                                                      && DateTimeOffset.TryParse(value.GetString(), out var dateTime) =>
                new IndexValue(expectedKind, null, null, dateTime, null),
            ScopedStorageIndexValueKind.Bool when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                new IndexValue(expectedKind, null, null, null, value.GetBoolean()),
            _ => throw new ArgumentException(
                $"Registration storage index value '{value.GetRawText()}' is not a valid {expectedKind} value.",
                nameof(value)),
        };
    }

    private static ScopedStorageIndexEntryDB CreateIndexEntry(
        ScopedStorageContractDescriptor contract,
        string key,
        string indexName,
        IndexValue value)
    {
        var entry = new ScopedStorageIndexEntryDB
        {
            Id = Guid.NewGuid(),
            SourceId = contract.SourceId,
            StorageName = contract.StorageName,
            IndexName = indexName,
            RecordKey = key,
        };

        switch (value.Kind)
        {
            case ScopedStorageIndexValueKind.String:
                entry.StringValue = value.StringValue;
                break;
            case ScopedStorageIndexValueKind.Number:
                entry.NumberValue = value.NumberValue;
                break;
            case ScopedStorageIndexValueKind.DateTime:
                entry.DateTimeValue = value.DateTimeValue;
                break;
            case ScopedStorageIndexValueKind.Bool:
                entry.BoolValue = value.BoolValue;
                break;
        }

        return entry;
    }

    private static string ApplyPatch(string valueJson, JsonElement patch)
    {
        var node = JsonNode.Parse(valueJson) as JsonObject
            ?? throw new ArgumentException("Registration storage claim can only patch JSON object records.");

        foreach (var property in patch.EnumerateObject())
            node[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return node.ToJsonString(JsonOptions);
    }

    private static void ValidateClaimPatchIndexedFields(
        ScopedStorageContractDescriptor contract,
        JsonElement patch,
        IReadOnlyDictionary<string, IReadOnlyList<IndexValue>> indexUpdates)
    {
        foreach (var property in patch.EnumerateObject())
        {
            if ((contract.Indexes ?? []).Any(index => string.Equals(index.Name, property.Name, StringComparison.Ordinal))
                && !indexUpdates.ContainsKey(property.Name))
            {
                throw new ArgumentException(
                    $"Registration storage claim patch changes indexed field '{property.Name}' " +
                    "without replacing that index value.",
                    nameof(patch));
            }
        }
    }

    private ScopedStorageContractDescriptor RequireContract(string SourceId, string storageName) =>
        contracts.FindStorageContract(SourceId, storageName)
        ?? throw new NotSupportedException(
            $"Registration '{SourceId}' has not declared host storage '{storageName}'.");

    private static void RequireOperation(
        ScopedStorageContractDescriptor contract,
        string operation)
    {
        if (!contract.Operations.Any(candidate =>
                string.Equals(candidate.Name, operation, StringComparison.Ordinal)))
        {
            throw new NotSupportedException(
                $"Registration storage operation '{operation}' is not declared for " +
                $"'{contract.SourceId}/{contract.StorageName}'.");
        }
    }

    private static ScopedStorageIndexDescriptor RequireIndex(
        ScopedStorageContractDescriptor contract,
        string indexName) =>
        (contract.Indexes ?? []).FirstOrDefault(index =>
            string.Equals(index.Name, indexName, StringComparison.Ordinal))
        ?? throw new NotSupportedException(
            $"Registration storage index '{indexName}' is not declared for " +
            $"'{contract.SourceId}/{contract.StorageName}'.");

    private static void RequireComparison(
        ScopedStorageIndexDescriptor descriptor,
        string comparisonOperator)
    {
        var isRange = comparisonOperator is
            ScopedStorageComparisonOperators.LessThanOrEqual or
            ScopedStorageComparisonOperators.GreaterThanOrEqual;

        if (comparisonOperator == ScopedStorageComparisonOperators.EqualTo && !descriptor.AllowsEquality)
            throw new NotSupportedException(
                $"Registration storage index '{descriptor.Name}' does not allow equality comparisons.");

        if (isRange && !descriptor.AllowsRange)
            throw new NotSupportedException(
                $"Registration storage index '{descriptor.Name}' does not allow range comparisons.");
    }

    private static void ValidateDocumentSize(
        ScopedStorageContractDescriptor contract,
        JsonElement value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (byteCount > contract.MaxDocumentBytes)
        {
            throw new ArgumentException(
                $"Registration storage document for '{contract.SourceId}/{contract.StorageName}' " +
                $"is {byteCount} bytes and exceeds the declared {contract.MaxDocumentBytes} byte limit.");
        }
    }

    private static string CommitKey(
        string SourceId,
        string storageName,
        string idempotencyKey) =>
        $"{SourceId}\u001f{storageName}\u001f{idempotencyKey}";

    private static string ClaimPrefix(string SourceId, string storageName) =>
        $"{SourceId}\u001f{storageName}\u001f";

    private static string ClaimKey(string SourceId, string storageName, string recordKey) =>
        ClaimPrefix(SourceId, storageName) + recordKey;

    private static ScopedStorageContractException ScopedStorageFailure(
        string code,
        string message,
        string? key = null,
        long? expectedRevision = null,
        long? actualRevision = null) =>
        new(new ScopedStorageContractFailure(code, message, key, expectedRevision, actualRevision));

    private static ScopedStorageContractException RevisionConflict(
        string key,
        long? expectedRevision,
        long actualRevision) =>
        ScopedStorageFailure(
            ScopedStorageErrors.RevisionConflict,
            "The registration storage record revision is stale.",
            key,
            expectedRevision,
            actualRevision);

    private static void ValidateAuthority(
        string SourceId,
        string storageName,
        string recordKey,
        ScopedStorageClaimAuthority? authority,
        long actualRevision)
    {
        if (authority is null)
            return;

        var claimKey = ClaimKey(SourceId, storageName, recordKey);
        if (!Claims.TryGetValue(claimKey, out var active) ||
            !active.IsValidAt(DateTimeOffset.UtcNow) ||
            !active.Matches(authority) ||
            authority.Revision != actualRevision)
        {
            throw ScopedStorageFailure(
                ScopedStorageErrors.FencingRejected,
                "The registration storage claim fence is stale.",
                recordKey,
                actualRevision,
                actualRevision);
        }
    }

    private static void AdvanceClaim(
        string SourceId,
        string storageName,
        string recordKey,
        ScopedStorageClaimAuthority? authority,
        long revision)
    {
        if (authority is null)
            return;

        var claimKey = ClaimKey(SourceId, storageName, recordKey);
        if (Claims.TryGetValue(claimKey, out var active) && active.Matches(authority))
            Claims[claimKey] = active with { Revision = revision };
    }

    private static ScopedStorageClaimAuthority NewClaimAuthority(
        string SourceId,
        string storageName,
        IReadOnlyList<ScopedStorageRecordDB>? records,
        long generation,
        ScopedStorageClaimAuthority? requested)
    {
        var revision = records is null || records.Count == 0
            ? requested?.Revision ?? 0
            : records.Max(Revision);
        if (requested is not null)
            return requested with { Revision = revision };

        return new ScopedStorageClaimAuthority(
            SourceId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(5),
            generation,
            revision);
    }

    private async Task<JsonElement> ReadIndexesAsync(
        ScopedStorageContractDescriptor contract,
        string key,
        CancellationToken ct)
    {
        var indexes = await Indexes(contract)
            .AsNoTracking()
            .Where(index => index.RecordKey == key)
            .ToListAsync(ct);
        return IndexesResponse(indexes);
    }

    private static JsonElement RecordsResponse(IReadOnlyList<ScopedStorageRecordDB> records)
    {
        var items = records.Select(record =>
        {
            using var value = JsonDocument.Parse(record.ValueJson);
            return new
            {
                key = record.RecordKey,
                value = value.RootElement.Clone(),
                revision = Revision(record),
            };
        });

        return JsonSerializer.SerializeToElement(new { records = items }, JsonOptions);
    }

    private static JsonElement IndexesResponse(IReadOnlyList<ScopedStorageIndexEntryDB> indexes) =>
        JsonSerializer.SerializeToElement(
            indexes.GroupBy(index => index.IndexName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(ToIndexValue).ToArray(),
                    StringComparer.Ordinal),
            JsonOptions);

    private static object ToIndexValue(ScopedStorageIndexEntryDB index) =>
        index.StringValue is not null ? index.StringValue
        : index.NumberValue is not null ? index.NumberValue.Value
        : index.DateTimeValue is not null ? index.DateTimeValue.Value
        : index.BoolValue is not null ? index.BoolValue.Value
        : null!;

    private static long Revision(ScopedStorageRecordDB record) =>
        Math.Max(0, record.UpdatedAt.UtcDateTime.Ticks);

    private static int CountRecords(JsonElement response) =>
        response.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array
            ? records.GetArrayLength()
            : 0;

    private static string ReadRequiredString(JsonElement parameters, string propertyName, int maxLength)
    {
        if (!parameters.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ArgumentException($"Registration storage parameter '{propertyName}' is required.", nameof(parameters));
        }

        return RequireIdentifier(property.GetString()!, propertyName, maxLength);
    }

    private static string? ReadOptionalString(JsonElement parameters, string propertyName, int maxLength)
    {
        if (!parameters.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Registration storage parameter '{propertyName}' must be a string.", nameof(parameters));

        return RequireIdentifier(property.GetString() ?? "", propertyName, maxLength);
    }

    private static int? ReadOptionalInt(
        JsonElement parameters,
        string propertyName,
        int min,
        int max)
    {
        if (!parameters.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw new ArgumentException($"Registration storage parameter '{propertyName}' must be an integer.", nameof(parameters));

        return Math.Clamp(value, min, max);
    }

    private static string RequireIdentifier(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Registration storage '{parameterName}' is required.", parameterName);
        if (value.Length > maxLength)
            throw new ArgumentException($"Registration storage '{parameterName}' cannot exceed {maxLength} characters.", parameterName);

        return value.Trim();
    }

    private static string NormalizeOperation(string operation) =>
        operation.ToLowerInvariant() switch
        {
            "get" => ScopedStorageOperations.Get,
            "upsert" => ScopedStorageOperations.Upsert,
            "batchupsert" => ScopedStorageOperations.BatchUpsert,
            "delete" => ScopedStorageOperations.Delete,
            "batchdelete" => ScopedStorageOperations.BatchDelete,
            "list" => ScopedStorageOperations.List,
            "query" => ScopedStorageOperations.Query,
            "claim" => ScopedStorageOperations.Claim,
            _ => operation,
        };

    private static string NormalizeComparisonOperator(string comparisonOperator) =>
        comparisonOperator.ToLowerInvariant() switch
        {
            "equals" => ScopedStorageComparisonOperators.EqualTo,
            "lessthanorequal" => ScopedStorageComparisonOperators.LessThanOrEqual,
            "greaterthanorequal" => ScopedStorageComparisonOperators.GreaterThanOrEqual,
            _ => throw new ArgumentException(
                $"Registration storage comparison operator '{comparisonOperator}' is not supported.",
                nameof(comparisonOperator)),
        };

    private static string NormalizeSortDirection(string direction) =>
        direction.ToLowerInvariant() switch
        {
            "asc" => ScopedStorageSortDirections.Ascending,
            "desc" => ScopedStorageSortDirections.Descending,
            _ => throw new ArgumentException(
                $"Registration storage sort direction '{direction}' is not supported.",
                nameof(direction)),
        };

    private sealed record StorageWrite(
        string Key,
        string ValueJson,
        IReadOnlyList<ScopedStorageIndexEntryDB> Indexes);

    private sealed record PendingMutation(
        ScopedStorageMutation Mutation,
        string Key,
        ScopedStorageRecordDB? Record,
        long ActualRevision,
        string? ValueJson,
        IReadOnlyList<ScopedStorageIndexEntryDB> Indexes);

    private sealed record StorageQuery(
        IReadOnlyList<StorageFilter> Filters,
        StorageOrder? OrderBy,
        int? Limit);

    private sealed record StorageClaim(
        StorageQuery Query,
        JsonElement Patch,
        IReadOnlyDictionary<string, IReadOnlyList<IndexValue>> IndexUpdates);

    private sealed record StorageFilter(
        string IndexName,
        string Operator,
        JsonElement Value);

    private sealed record StorageOrder(
        string IndexName,
        string Direction);

    private sealed record IndexValue(
        ScopedStorageIndexValueKind Kind,
        string? StringValue,
        double? NumberValue,
        DateTimeOffset? DateTimeValue,
        bool? BoolValue);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(
            Type typeToConvert,
            JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlySetJsonConverter<>).MakeGenericType(
                    typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
        where T : notnull
    {
        public override IReadOnlySet<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new HashSet<T>(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
