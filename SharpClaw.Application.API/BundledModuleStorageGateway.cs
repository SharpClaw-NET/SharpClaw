using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API;

public sealed class BundledModuleStorageGateway(
    SharpClawDbContext db,
    IModuleStorageContractProvider contracts,
    IModuleStorageTelemetry? telemetry = null) : IModuleStorageGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
        contracts.GetStorageContracts();

    public async Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default)
    {
        moduleId = RequireIdentifier(moduleId, nameof(moduleId), 128);
        storageName = RequireIdentifier(storageName, nameof(storageName), 128);
        operation = NormalizeOperation(RequireIdentifier(operation, nameof(operation), 64));

        var contract = RequireContract(moduleId, storageName);
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
                ModuleStorageOperations.Get => await GetAsync(contract, parameters, ct),
                ModuleStorageOperations.Upsert => await UpsertAsync(contract, parameters, ct),
                ModuleStorageOperations.BatchUpsert => await BatchUpsertAsync(contract, parameters, ct),
                ModuleStorageOperations.Delete => await DeleteAsync(contract, parameters, ct),
                ModuleStorageOperations.BatchDelete => await BatchDeleteAsync(contract, parameters, ct),
                ModuleStorageOperations.List => await ListAsync(contract, parameters, ct),
                ModuleStorageOperations.Query => await QueryAsync(contract, parameters, ct),
                ModuleStorageOperations.Claim => await ClaimAsync(contract, parameters, ct),
                _ => throw new NotSupportedException(
                    $"Module storage operation '{operation}' is not supported."),
            };

            outputBytes = Encoding.UTF8.GetByteCount(result.GetRawText());
            recordCount = CountRecords(result);
            success = true;
            return result;
        }
        finally
        {
            telemetry?.Record(new ModuleStorageTelemetryEvent(
                moduleId,
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
        ModuleStorageContractDescriptor contract,
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
        }, JsonOptions);
    }

    private async Task<JsonElement> UpsertAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var write = ReadWrite(contract, parameters);
        await UpsertRecordAsync(contract, write, ct);
        await db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { saved = true }, JsonOptions);
    }

    private async Task<JsonElement> BatchUpsertAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var writes = ReadWrites(contract, parameters);
        foreach (var write in writes)
            await UpsertRecordAsync(contract, write, ct);

        if (writes.Count > 0)
            await db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new { saved = writes.Count }, JsonOptions);
    }

    private async Task UpsertRecordAsync(
        ModuleStorageContractDescriptor contract,
        StorageWrite write,
        CancellationToken ct)
    {
        var record = await Records(contract)
            .SingleOrDefaultAsync(record => record.RecordKey == write.Key, ct);
        if (record is null)
        {
            record = new ModuleStorageRecordDB
            {
                Id = Guid.NewGuid(),
                ModuleId = contract.ModuleId,
                StorageName = contract.StorageName,
                RecordKey = write.Key,
                ValueJson = write.ValueJson,
            };
            db.ModuleStorageRecords.Add(record);
        }
        else
        {
            record.ValueJson = write.ValueJson;
        }

        await DeleteIndexesAsync(contract, write.Key, ct);
        db.ModuleStorageIndexEntries.AddRange(write.Indexes);
    }

    private async Task<JsonElement> DeleteAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var key = ReadRequiredString(parameters, "key", 256);
        var record = await Records(contract)
            .SingleOrDefaultAsync(record => record.RecordKey == key, ct);
        var deleted = record is not null;

        if (record is not null)
            db.ModuleStorageRecords.Remove(record);

        var removedIndexes = await DeleteIndexesAsync(contract, key, ct);
        if (deleted || removedIndexes)
            await db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new { deleted }, JsonOptions);
    }

    private async Task<JsonElement> BatchDeleteAsync(
        ModuleStorageContractDescriptor contract,
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

        db.ModuleStorageRecords.RemoveRange(records);
        db.ModuleStorageIndexEntries.RemoveRange(indexes);
        await db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new { deleted = records.Count }, JsonOptions);
    }

    private async Task<JsonElement> ListAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var offset = ReadOptionalInt(parameters, "offset", 0, 100_000) ?? 0;
        var limit = ReadOptionalInt(parameters, "limit", 1, 1_000);
        IQueryable<ModuleStorageRecordDB> query = Records(contract)
            .AsNoTracking()
            .OrderBy(record => record.RecordKey)
            .Skip(offset);
        if (limit is { } take)
            query = query.Take(take);

        return RecordsResponse(await query.ToListAsync(ct));
    }

    private async Task<JsonElement> QueryAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var query = ReadQuery(contract, parameters);
        var records = await LoadQueryRecordsAsync(contract, query, tracking: false, ct);
        return RecordsResponse(records);
    }

    private async Task<JsonElement> ClaimAsync(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters,
        CancellationToken ct)
    {
        var claim = ReadClaim(contract, parameters);
        await using var transaction = await BeginClaimTransactionAsync(ct);

        var records = await LoadQueryRecordsAsync(contract, claim.Query, tracking: true, ct);
        if (records.Count == 0)
        {
            if (transaction is not null)
                await transaction.CommitAsync(ct);
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

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return RecordsResponse(records);
    }

    private async Task<IReadOnlyList<ModuleStorageRecordDB>> LoadQueryRecordsAsync(
        ModuleStorageContractDescriptor contract,
        StorageQuery query,
        bool tracking,
        CancellationToken ct)
    {
        if (query.Filters.Count == 0 && query.OrderBy is null)
            throw new ArgumentException("Module storage query requires at least one filter or order index.");

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
        ModuleStorageContractDescriptor contract,
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
        ModuleStorageContractDescriptor contract,
        StorageOrder order,
        HashSet<string> filteredKeys,
        int limit,
        CancellationToken ct)
    {
        var descriptor = RequireIndex(contract, order.IndexName);
        var descending = string.Equals(
            order.Direction,
            ModuleStorageSortDirections.Descending,
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

    private async Task<IReadOnlyList<ModuleStorageRecordDB>> LoadRecordsByKeysAsync(
        ModuleStorageContractDescriptor contract,
        IReadOnlyList<string> keys,
        bool tracking,
        CancellationToken ct)
    {
        if (keys.Count == 0)
            return [];

        var keySet = keys.ToArray();
        IQueryable<ModuleStorageRecordDB> query = Records(contract)
            .Where(record => keySet.Contains(record.RecordKey));
        if (!tracking)
            query = query.AsNoTracking();

        var records = await query.ToListAsync(ct);
        var byKey = records.ToDictionary(record => record.RecordKey, StringComparer.Ordinal);
        var ordered = new List<ModuleStorageRecordDB>();
        foreach (var key in keys)
        {
            if (byKey.TryGetValue(key, out var record))
                ordered.Add(record);
        }

        return ordered;
    }

    private async Task ReplaceClaimIndexesAsync(
        ModuleStorageContractDescriptor contract,
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
        db.ModuleStorageIndexEntries.RemoveRange(existing);

        foreach (var key in keys)
        {
            foreach (var (indexName, values) in indexUpdates)
            {
                foreach (var value in values)
                    db.ModuleStorageIndexEntries.Add(CreateIndexEntry(contract, key, indexName, value));
            }
        }
    }

    private async Task<IDbContextTransaction?> BeginClaimTransactionAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.Database.IsInMemory())
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    }

    private IQueryable<ModuleStorageRecordDB> Records(ModuleStorageContractDescriptor contract) =>
        db.ModuleStorageRecords.Where(record =>
            record.ModuleId == contract.ModuleId
            && record.StorageName == contract.StorageName);

    private IQueryable<ModuleStorageIndexEntryDB> Indexes(ModuleStorageContractDescriptor contract) =>
        db.ModuleStorageIndexEntries.Where(index =>
            index.ModuleId == contract.ModuleId
            && index.StorageName == contract.StorageName);

    private static IQueryable<ModuleStorageIndexEntryDB> ApplyComparison(
        IQueryable<ModuleStorageIndexEntryDB> query,
        ModuleStorageIndexValueKind valueKind,
        string comparisonOperator,
        IndexValue value) =>
        valueKind switch
        {
            ModuleStorageIndexValueKind.String => comparisonOperator switch
            {
                ModuleStorageComparisonOperators.EqualTo => query.Where(index => index.StringValue == value.StringValue),
                _ => throw new ArgumentException("String index values only support equality comparisons."),
            },
            ModuleStorageIndexValueKind.Number => comparisonOperator switch
            {
                ModuleStorageComparisonOperators.EqualTo => query.Where(index => index.NumberValue == value.NumberValue),
                ModuleStorageComparisonOperators.LessThanOrEqual => query.Where(index => index.NumberValue <= value.NumberValue),
                ModuleStorageComparisonOperators.GreaterThanOrEqual => query.Where(index => index.NumberValue >= value.NumberValue),
                _ => query,
            },
            ModuleStorageIndexValueKind.DateTime => comparisonOperator switch
            {
                ModuleStorageComparisonOperators.EqualTo => query.Where(index => index.DateTimeValue == value.DateTimeValue),
                ModuleStorageComparisonOperators.LessThanOrEqual => query.Where(index => index.DateTimeValue <= value.DateTimeValue),
                ModuleStorageComparisonOperators.GreaterThanOrEqual => query.Where(index => index.DateTimeValue >= value.DateTimeValue),
                _ => query,
            },
            ModuleStorageIndexValueKind.Bool => comparisonOperator switch
            {
                ModuleStorageComparisonOperators.EqualTo => query.Where(index => index.BoolValue == value.BoolValue),
                _ => throw new ArgumentException("Boolean index values only support equality comparisons."),
            },
            _ => query,
        };

    private static IOrderedQueryable<ModuleStorageIndexEntryDB> OrderIndexes(
        IQueryable<ModuleStorageIndexEntryDB> query,
        ModuleStorageIndexValueKind valueKind,
        bool descending) =>
        (valueKind, descending) switch
        {
            (ModuleStorageIndexValueKind.String, false) => query
                .OrderBy(index => index.StringValue)
                .ThenBy(index => index.RecordKey),
            (ModuleStorageIndexValueKind.String, true) => query
                .OrderByDescending(index => index.StringValue)
                .ThenByDescending(index => index.RecordKey),
            (ModuleStorageIndexValueKind.Number, false) => query
                .OrderBy(index => index.NumberValue)
                .ThenBy(index => index.RecordKey),
            (ModuleStorageIndexValueKind.Number, true) => query
                .OrderByDescending(index => index.NumberValue)
                .ThenByDescending(index => index.RecordKey),
            (ModuleStorageIndexValueKind.DateTime, false) => query
                .OrderBy(index => index.DateTimeValue)
                .ThenBy(index => index.RecordKey),
            (ModuleStorageIndexValueKind.DateTime, true) => query
                .OrderByDescending(index => index.DateTimeValue)
                .ThenByDescending(index => index.RecordKey),
            (ModuleStorageIndexValueKind.Bool, false) => query
                .OrderBy(index => index.BoolValue)
                .ThenBy(index => index.RecordKey),
            (ModuleStorageIndexValueKind.Bool, true) => query
                .OrderByDescending(index => index.BoolValue)
                .ThenByDescending(index => index.RecordKey),
            _ => query.OrderBy(index => index.RecordKey),
        };

    private async Task<bool> DeleteIndexesAsync(
        ModuleStorageContractDescriptor contract,
        string key,
        CancellationToken ct)
    {
        var indexes = await Indexes(contract)
            .Where(index => index.RecordKey == key)
            .ToListAsync(ct);
        db.ModuleStorageIndexEntries.RemoveRange(indexes);
        return indexes.Count > 0;
    }

    private StorageWrite ReadWrite(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var key = ReadRequiredString(parameters, "key", 256);
        if (!parameters.TryGetProperty("value", out var value)
            || value.ValueKind is JsonValueKind.Undefined)
        {
            throw new ArgumentException("Module storage upsert requires a value.", nameof(parameters));
        }

        ValidateDocumentSize(contract, value);
        var indexes = parameters.TryGetProperty("indexes", out var indexElement)
            && indexElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? ReadIndexes(contract, key, indexElement)
                : [];

        return new StorageWrite(key, value.GetRawText(), indexes);
    }

    private List<StorageWrite> ReadWrites(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Module storage batchUpsert requires a records array.", nameof(parameters));
        }

        var writes = records.EnumerateArray()
            .Select(record => ReadWrite(contract, record))
            .ToList();
        if (writes.Count > contract.MaxBatchSize)
        {
            throw new ArgumentException(
                $"Module storage batchUpsert for '{contract.ModuleId}/{contract.StorageName}' " +
                $"cannot exceed {contract.MaxBatchSize} records.",
                nameof(parameters));
        }

        return writes;
    }

    private static List<string> ReadKeys(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Module storage batchDelete requires a keys array.", nameof(parameters));
        }

        var result = keys.EnumerateArray()
            .Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Module storage batchDelete keys must be strings.", nameof(parameters));
                return RequireIdentifier(value.GetString() ?? "", "key", 256);
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (result.Count > contract.MaxBatchSize)
        {
            throw new ArgumentException(
                $"Module storage batchDelete for '{contract.ModuleId}/{contract.StorageName}' " +
                $"cannot exceed {contract.MaxBatchSize} keys.",
                nameof(parameters));
        }

        return result;
    }

    private StorageQuery ReadQuery(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var filters = ReadFilters(contract, parameters);
        var order = ReadOrder(contract, parameters);
        var limit = ReadOptionalInt(parameters, "limit", 1, 1_000);
        return new StorageQuery(filters, order, limit);
    }

    private StorageClaim ReadClaim(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        var query = ReadQuery(contract, parameters);
        if (!parameters.TryGetProperty("patch", out var patch)
            || patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Module storage claim requires an object patch.", nameof(parameters));
        }

        var indexes = parameters.TryGetProperty("indexes", out var indexElement)
            && indexElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? ReadIndexUpdates(contract, indexElement)
                : new Dictionary<string, IReadOnlyList<IndexValue>>(StringComparer.Ordinal);

        return new StorageClaim(query, patch.Clone(), indexes);
    }

    private static IReadOnlyList<StorageFilter> ReadFilters(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("filters", out var filtersElement)
            || filtersElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (filtersElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Module storage query filters must be an array.", nameof(parameters));

        var filters = new List<StorageFilter>();
        foreach (var element in filtersElement.EnumerateArray())
        {
            var indexName = ReadRequiredString(element, "indexName", 128);
            var comparisonOperator = NormalizeComparisonOperator(
                ReadRequiredString(element, "operator", 64));
            if (!element.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new ArgumentException("Module storage query filters require a value.", nameof(parameters));
            }

            var descriptor = RequireIndex(contract, indexName);
            RequireComparison(descriptor, comparisonOperator);
            _ = ReadIndexValue(value, descriptor.ValueKind);
            filters.Add(new StorageFilter(indexName, comparisonOperator, value.Clone()));
        }

        return filters;
    }

    private static StorageOrder? ReadOrder(
        ModuleStorageContractDescriptor contract,
        JsonElement parameters)
    {
        if (!parameters.TryGetProperty("orderBy", out var orderElement)
            || orderElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (orderElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Module storage orderBy must be an object.", nameof(parameters));

        var indexName = ReadRequiredString(orderElement, "indexName", 128);
        var direction = NormalizeSortDirection(
            ReadOptionalString(orderElement, "direction", 16)
            ?? ModuleStorageSortDirections.Ascending);
        _ = RequireIndex(contract, indexName);
        return new StorageOrder(indexName, direction);
    }

    private static IReadOnlyList<ModuleStorageIndexEntryDB> ReadIndexes(
        ModuleStorageContractDescriptor contract,
        string key,
        JsonElement indexes)
    {
        return ReadIndexUpdates(contract, indexes)
            .SelectMany(update => update.Value.Select(value =>
                CreateIndexEntry(contract, key, update.Key, value)))
            .ToList();
    }

    private static Dictionary<string, IReadOnlyList<IndexValue>> ReadIndexUpdates(
        ModuleStorageContractDescriptor contract,
        JsonElement indexes)
    {
        if (indexes.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Module storage indexes must be a JSON object.", nameof(indexes));

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
        ModuleStorageIndexValueKind expectedKind)
    {
        return expectedKind switch
        {
            ModuleStorageIndexValueKind.String when value.ValueKind == JsonValueKind.String =>
                new IndexValue(expectedKind, value.GetString() ?? "", null, null, null),
            ModuleStorageIndexValueKind.Number when value.ValueKind == JsonValueKind.Number
                                                 && value.TryGetDouble(out var number) =>
                new IndexValue(expectedKind, null, number, null, null),
            ModuleStorageIndexValueKind.DateTime when value.ValueKind == JsonValueKind.String
                                                      && DateTimeOffset.TryParse(value.GetString(), out var dateTime) =>
                new IndexValue(expectedKind, null, null, dateTime, null),
            ModuleStorageIndexValueKind.Bool when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                new IndexValue(expectedKind, null, null, null, value.GetBoolean()),
            _ => throw new ArgumentException(
                $"Module storage index value '{value.GetRawText()}' is not a valid {expectedKind} value.",
                nameof(value)),
        };
    }

    private static ModuleStorageIndexEntryDB CreateIndexEntry(
        ModuleStorageContractDescriptor contract,
        string key,
        string indexName,
        IndexValue value)
    {
        var entry = new ModuleStorageIndexEntryDB
        {
            Id = Guid.NewGuid(),
            ModuleId = contract.ModuleId,
            StorageName = contract.StorageName,
            IndexName = indexName,
            RecordKey = key,
        };

        switch (value.Kind)
        {
            case ModuleStorageIndexValueKind.String:
                entry.StringValue = value.StringValue;
                break;
            case ModuleStorageIndexValueKind.Number:
                entry.NumberValue = value.NumberValue;
                break;
            case ModuleStorageIndexValueKind.DateTime:
                entry.DateTimeValue = value.DateTimeValue;
                break;
            case ModuleStorageIndexValueKind.Bool:
                entry.BoolValue = value.BoolValue;
                break;
        }

        return entry;
    }

    private static string ApplyPatch(string valueJson, JsonElement patch)
    {
        var node = JsonNode.Parse(valueJson) as JsonObject
            ?? throw new ArgumentException("Module storage claim can only patch JSON object records.");

        foreach (var property in patch.EnumerateObject())
            node[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return node.ToJsonString(JsonOptions);
    }

    private static void ValidateClaimPatchIndexedFields(
        ModuleStorageContractDescriptor contract,
        JsonElement patch,
        IReadOnlyDictionary<string, IReadOnlyList<IndexValue>> indexUpdates)
    {
        foreach (var property in patch.EnumerateObject())
        {
            if ((contract.Indexes ?? []).Any(index => string.Equals(index.Name, property.Name, StringComparison.Ordinal))
                && !indexUpdates.ContainsKey(property.Name))
            {
                throw new ArgumentException(
                    $"Module storage claim patch changes indexed field '{property.Name}' " +
                    "without replacing that index value.",
                    nameof(patch));
            }
        }
    }

    private ModuleStorageContractDescriptor RequireContract(string moduleId, string storageName) =>
        contracts.FindStorageContract(moduleId, storageName)
        ?? throw new NotSupportedException(
            $"Module '{moduleId}' has not declared host storage '{storageName}'.");

    private static void RequireOperation(
        ModuleStorageContractDescriptor contract,
        string operation)
    {
        if (!contract.Operations.Any(candidate =>
                string.Equals(candidate.Name, operation, StringComparison.Ordinal)))
        {
            throw new NotSupportedException(
                $"Module storage operation '{operation}' is not declared for " +
                $"'{contract.ModuleId}/{contract.StorageName}'.");
        }
    }

    private static ModuleStorageIndexDescriptor RequireIndex(
        ModuleStorageContractDescriptor contract,
        string indexName) =>
        (contract.Indexes ?? []).FirstOrDefault(index =>
            string.Equals(index.Name, indexName, StringComparison.Ordinal))
        ?? throw new NotSupportedException(
            $"Module storage index '{indexName}' is not declared for " +
            $"'{contract.ModuleId}/{contract.StorageName}'.");

    private static void RequireComparison(
        ModuleStorageIndexDescriptor descriptor,
        string comparisonOperator)
    {
        var isRange = comparisonOperator is
            ModuleStorageComparisonOperators.LessThanOrEqual or
            ModuleStorageComparisonOperators.GreaterThanOrEqual;

        if (comparisonOperator == ModuleStorageComparisonOperators.EqualTo && !descriptor.AllowsEquality)
            throw new NotSupportedException(
                $"Module storage index '{descriptor.Name}' does not allow equality comparisons.");

        if (isRange && !descriptor.AllowsRange)
            throw new NotSupportedException(
                $"Module storage index '{descriptor.Name}' does not allow range comparisons.");
    }

    private static void ValidateDocumentSize(
        ModuleStorageContractDescriptor contract,
        JsonElement value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (byteCount > contract.MaxDocumentBytes)
        {
            throw new ArgumentException(
                $"Module storage document for '{contract.ModuleId}/{contract.StorageName}' " +
                $"is {byteCount} bytes and exceeds the declared {contract.MaxDocumentBytes} byte limit.");
        }
    }

    private static JsonElement RecordsResponse(IReadOnlyList<ModuleStorageRecordDB> records)
    {
        var items = records.Select(record =>
        {
            using var value = JsonDocument.Parse(record.ValueJson);
            return new
            {
                key = record.RecordKey,
                value = value.RootElement.Clone(),
            };
        });

        return JsonSerializer.SerializeToElement(new { records = items }, JsonOptions);
    }

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
            throw new ArgumentException($"Module storage parameter '{propertyName}' is required.", nameof(parameters));
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
            throw new ArgumentException($"Module storage parameter '{propertyName}' must be a string.", nameof(parameters));

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
            throw new ArgumentException($"Module storage parameter '{propertyName}' must be an integer.", nameof(parameters));

        return Math.Clamp(value, min, max);
    }

    private static string RequireIdentifier(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Module storage '{parameterName}' is required.", parameterName);
        if (value.Length > maxLength)
            throw new ArgumentException($"Module storage '{parameterName}' cannot exceed {maxLength} characters.", parameterName);

        return value.Trim();
    }

    private static string NormalizeOperation(string operation) =>
        operation.ToLowerInvariant() switch
        {
            "get" => ModuleStorageOperations.Get,
            "upsert" => ModuleStorageOperations.Upsert,
            "batchupsert" => ModuleStorageOperations.BatchUpsert,
            "delete" => ModuleStorageOperations.Delete,
            "batchdelete" => ModuleStorageOperations.BatchDelete,
            "list" => ModuleStorageOperations.List,
            "query" => ModuleStorageOperations.Query,
            "claim" => ModuleStorageOperations.Claim,
            _ => operation,
        };

    private static string NormalizeComparisonOperator(string comparisonOperator) =>
        comparisonOperator.ToLowerInvariant() switch
        {
            "equals" => ModuleStorageComparisonOperators.EqualTo,
            "lessthanorequal" => ModuleStorageComparisonOperators.LessThanOrEqual,
            "greaterthanorequal" => ModuleStorageComparisonOperators.GreaterThanOrEqual,
            _ => throw new ArgumentException(
                $"Module storage comparison operator '{comparisonOperator}' is not supported.",
                nameof(comparisonOperator)),
        };

    private static string NormalizeSortDirection(string direction) =>
        direction.ToLowerInvariant() switch
        {
            "asc" => ModuleStorageSortDirections.Ascending,
            "desc" => ModuleStorageSortDirections.Descending,
            _ => throw new ArgumentException(
                $"Module storage sort direction '{direction}' is not supported.",
                nameof(direction)),
        };

    private sealed record StorageWrite(
        string Key,
        string ValueJson,
        IReadOnlyList<ModuleStorageIndexEntryDB> Indexes);

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
        ModuleStorageIndexValueKind Kind,
        string? StringValue,
        double? NumberValue,
        DateTimeOffset? DateTimeValue,
        bool? BoolValue);
}
