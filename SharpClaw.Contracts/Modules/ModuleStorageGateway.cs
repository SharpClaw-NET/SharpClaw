using System.Text.Json;

namespace SharpClaw.Contracts.Modules;

public interface IModuleStorageGateway
{
    IReadOnlyList<ModuleStorageContractDescriptor> ListContracts();

    Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default);
}

public interface IModuleStorageContractProvider
{
    IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts();

    ModuleStorageContractDescriptor? FindStorageContract(
        string moduleId,
        string storageName);
}

public static class ModuleStorageOperations
{
    public const string Get = "get";
    public const string Upsert = "upsert";
    public const string BatchUpsert = "batchUpsert";
    public const string Delete = "delete";
    public const string BatchDelete = "batchDelete";
    public const string List = "list";
    public const string Query = "query";
    public const string Claim = "claim";
}

public static class ModuleStorageComparisonOperators
{
    public const string EqualTo = "equals";
    public const string LessThanOrEqual = "lessThanOrEqual";
    public const string GreaterThanOrEqual = "greaterThanOrEqual";
}

public static class ModuleStorageSortDirections
{
    public const string Ascending = "asc";
    public const string Descending = "desc";
}

public sealed record ModuleStorageContractDescriptor(
    string ModuleId,
    string StorageName,
    IReadOnlyList<ModuleStorageOperationDescriptor> Operations,
    string? Description = null,
    IReadOnlyList<ModuleStorageIndexDescriptor>? Indexes = null,
    int MaxDocumentBytes = 65_536,
    int MaxBatchSize = 100);

public sealed record ModuleStorageOperationDescriptor(
    string Name,
    string? Description = null);

public sealed record ModuleStorageIndexDescriptor(
    string Name,
    ModuleStorageIndexValueKind ValueKind,
    bool AllowsEquality = true,
    bool AllowsRange = false);

public enum ModuleStorageIndexValueKind
{
    String,
    Number,
    DateTime,
    Bool,
}

public sealed record ModuleDocumentIndexFilter(
    string IndexName,
    string Operator,
    object? Value);

public sealed record ModuleDocumentIndexOrder(
    string IndexName,
    string Direction = ModuleStorageSortDirections.Ascending);

public sealed record ModuleDocumentQueryPayload(
    IReadOnlyList<ModuleDocumentIndexFilter> Filters,
    ModuleDocumentIndexOrder? OrderBy = null,
    int? Limit = null);

public sealed record ModuleDocumentClaimPayload(
    IReadOnlyList<ModuleDocumentIndexFilter> Filters,
    ModuleDocumentIndexOrder? OrderBy,
    int? Limit,
    object Patch,
    object? Indexes = null);

public sealed record ModuleDocumentWrite<T>(
    string Key,
    T Value,
    object? Indexes = null);

public sealed class ModuleDocumentStore<T>(
    IModuleStorageGateway gateway,
    string moduleId,
    string storageName,
    JsonSerializerOptions? jsonOptions = null)
{
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<T?> GetAsync(string key, CancellationToken ct = default)
    {
        using var parameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { key }, _jsonOptions));
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.Get,
            parameters.RootElement,
            ct);

        if (!response.TryGetProperty("found", out var found)
            || found.ValueKind != JsonValueKind.True
            || !response.TryGetProperty("value", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return value.Deserialize<T>(_jsonOptions);
    }

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default)
    {
        using var parameters = JsonDocument.Parse("{}");
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.List,
            parameters.RootElement,
            ct);

        return DeserializeRecords(response);
    }

    public ModuleDocumentQuery<T> Query() => new(this);

    public ModuleDocumentClaim<T> Claim() => new(this);

    public async Task UpsertAsync(
        string key,
        T value,
        object? indexes = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["value"] = value,
        };
        if (indexes is not null) payload["indexes"] = indexes;

        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(payload, _jsonOptions));
        await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.Upsert,
            parameters.RootElement,
            ct);
    }

    public async Task<int> UpsertManyAsync(
        IEnumerable<ModuleDocumentWrite<T>> records,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var payload = new
        {
            records = records.Select(record => new
            {
                key = record.Key,
                value = record.Value,
                indexes = record.Indexes,
            }).ToArray(),
        };

        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(payload, _jsonOptions));
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.BatchUpsert,
            parameters.RootElement,
            ct);

        return response.TryGetProperty("saved", out var saved) && saved.TryGetInt32(out var count)
            ? count
            : 0;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        using var parameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { key }, _jsonOptions));
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.Delete,
            parameters.RootElement,
            ct);

        return response.TryGetProperty("deleted", out var deleted)
               && deleted.ValueKind == JsonValueKind.True;
    }

    public async Task<int> DeleteManyAsync(
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        using var parameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { keys = keys.ToArray() }, _jsonOptions));
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            ModuleStorageOperations.BatchDelete,
            parameters.RootElement,
            ct);

        return response.TryGetProperty("deleted", out var deleted) && deleted.TryGetInt32(out var count)
            ? count
            : 0;
    }

    internal Task<IReadOnlyList<T>> QueryAsync(
        ModuleDocumentQueryPayload payload,
        CancellationToken ct) =>
        InvokeRecordsAsync(ModuleStorageOperations.Query, payload, ct);

    internal Task<IReadOnlyList<T>> ClaimAsync(
        ModuleDocumentClaimPayload payload,
        CancellationToken ct) =>
        InvokeRecordsAsync(ModuleStorageOperations.Claim, payload, ct);

    private async Task<IReadOnlyList<T>> InvokeRecordsAsync(
        string operation,
        object payload,
        CancellationToken ct)
    {
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(payload, _jsonOptions));
        var response = await gateway.InvokeAsync(
            moduleId,
            storageName,
            operation,
            parameters.RootElement,
            ct);

        return DeserializeRecords(response);
    }

    private IReadOnlyList<T> DeserializeRecords(JsonElement response)
    {
        if (!response.TryGetProperty("records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<T>();
        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (value.Deserialize<T>(_jsonOptions) is { } item)
                result.Add(item);
        }

        return result;
    }
}

public sealed class ModuleDocumentQuery<T>
{
    private readonly ModuleDocumentStore<T> _store;
    private readonly List<ModuleDocumentIndexFilter> _filters = [];
    private ModuleDocumentIndexOrder? _orderBy;
    private int? _limit;

    internal ModuleDocumentQuery(ModuleDocumentStore<T> store)
    {
        _store = store;
    }

    public ModuleDocumentIndexFilterBuilder<ModuleDocumentQuery<T>, T> WhereIndex(string indexName) =>
        new(this, indexName);

    public ModuleDocumentQuery<T> OrderByIndex(string indexName) =>
        SetOrder(indexName, ModuleStorageSortDirections.Ascending);

    public ModuleDocumentQuery<T> OrderByIndexDescending(string indexName) =>
        SetOrder(indexName, ModuleStorageSortDirections.Descending);

    public ModuleDocumentQuery<T> Take(int limit)
    {
        _limit = limit;
        return this;
    }

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken ct = default) =>
        _store.QueryAsync(new ModuleDocumentQueryPayload(_filters.ToArray(), _orderBy, _limit), ct);

    internal ModuleDocumentQuery<T> AddFilter(
        string indexName,
        string comparisonOperator,
        object? value)
    {
        _filters.Add(new ModuleDocumentIndexFilter(indexName, comparisonOperator, value));
        return this;
    }

    private ModuleDocumentQuery<T> SetOrder(string indexName, string direction)
    {
        _orderBy = new ModuleDocumentIndexOrder(indexName, direction);
        return this;
    }
}

public sealed class ModuleDocumentClaim<T>
{
    private readonly ModuleDocumentStore<T> _store;
    private readonly List<ModuleDocumentIndexFilter> _filters = [];
    private ModuleDocumentIndexOrder? _orderBy;
    private int? _limit;
    private object? _patch;
    private object? _indexes;

    internal ModuleDocumentClaim(ModuleDocumentStore<T> store)
    {
        _store = store;
    }

    public ModuleDocumentIndexFilterBuilder<ModuleDocumentClaim<T>, T> WhereIndex(string indexName) =>
        new(this, indexName);

    public ModuleDocumentClaim<T> OrderByIndex(string indexName) =>
        SetOrder(indexName, ModuleStorageSortDirections.Ascending);

    public ModuleDocumentClaim<T> OrderByIndexDescending(string indexName) =>
        SetOrder(indexName, ModuleStorageSortDirections.Descending);

    public ModuleDocumentClaim<T> Take(int limit)
    {
        _limit = limit;
        return this;
    }

    public ModuleDocumentClaim<T> Patch(object patch, object? indexes = null)
    {
        _patch = patch;
        _indexes = indexes;
        return this;
    }

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken ct = default)
    {
        if (_patch is null)
            throw new InvalidOperationException("Module storage claim requires a patch before execution.");

        return _store.ClaimAsync(
            new ModuleDocumentClaimPayload(_filters.ToArray(), _orderBy, _limit, _patch, _indexes),
            ct);
    }

    internal ModuleDocumentClaim<T> AddFilter(
        string indexName,
        string comparisonOperator,
        object? value)
    {
        _filters.Add(new ModuleDocumentIndexFilter(indexName, comparisonOperator, value));
        return this;
    }

    private ModuleDocumentClaim<T> SetOrder(string indexName, string direction)
    {
        _orderBy = new ModuleDocumentIndexOrder(indexName, direction);
        return this;
    }
}

public sealed class ModuleDocumentIndexFilterBuilder<TQuery, TDocument>
{
    private readonly TQuery _query;
    private readonly string _indexName;

    internal ModuleDocumentIndexFilterBuilder(TQuery query, string indexName)
    {
        _query = query;
        _indexName = indexName;
    }

    public TQuery EqualTo(object? value) =>
        Add(ModuleStorageComparisonOperators.EqualTo, value);

    public TQuery LessThanOrEqual(object? value) =>
        Add(ModuleStorageComparisonOperators.LessThanOrEqual, value);

    public TQuery GreaterThanOrEqual(object? value) =>
        Add(ModuleStorageComparisonOperators.GreaterThanOrEqual, value);

    private TQuery Add(string comparisonOperator, object? value)
    {
        return _query switch
        {
            ModuleDocumentQuery<TDocument> query =>
                (TQuery)(object)query.AddFilter(_indexName, comparisonOperator, value),
            ModuleDocumentClaim<TDocument> claim =>
                (TQuery)(object)claim.AddFilter(_indexName, comparisonOperator, value),
            _ => throw new InvalidOperationException(
                $"Unsupported module document query builder '{typeof(TQuery).Name}'."),
        };
    }
}
