using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.DTOs.Editor;

namespace SharpClaw.Application.API;

public sealed class BundledModuleStorageGateway(IServiceProvider services) : IModuleStorageGateway
{
    private const string AgentOrchestrationModuleId = "sharpclaw_agent_orchestration";
    private const string EditorCommonModuleId = "sharpclaw_editor_common";
    private const string LlamaSharpModuleId = "sharpclaw_providers_llamasharp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyList<ModuleStorageContractDescriptor> Contracts =
    [
        new(
            AgentOrchestrationModuleId,
            "scheduled_jobs",
            Operations(
                "create",
                "get",
                "list",
                "update",
                "delete",
                "pause",
                "resume",
                "preview_job",
                "preview_expression",
                "list_ids",
                "lookup_items"),
            "Parent-backed scheduled job storage and cron preview operations."),
        new(
            AgentOrchestrationModuleId,
            "skills",
            Operations(
                "create",
                "get",
                "list",
                "update",
                "delete",
                "access",
                "list_ids",
                "lookup_items"),
            "Parent-backed Agent Orchestration skill storage."),
        new(
            EditorCommonModuleId,
            "editor_sessions",
            Operations(
                "create",
                "get",
                "list",
                "update",
                "delete",
                "active_connections",
                "list_ids",
                "lookup_items"),
            "Parent-backed editor session storage and active bridge state."),
        new(
            LlamaSharpModuleId,
            "local_models",
            Operations(
                "list",
                "delete",
                "set_mmproj",
                "list_available_files",
                "ready_file_path",
                "source_url"),
            "Parent-backed LlamaSharp local model records and lookup operations."),
    ];

    public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => Contracts;

    public Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement parameters,
        CancellationToken ct = default) =>
        (moduleId, storageName) switch
        {
            (AgentOrchestrationModuleId, "scheduled_jobs") =>
                InvokeScheduledJobsAsync(operation, parameters, ct),
            (AgentOrchestrationModuleId, "skills") =>
                InvokeSkillsAsync(operation, parameters, ct),
            (EditorCommonModuleId, "editor_sessions") =>
                InvokeEditorSessionsAsync(operation, parameters, ct),
            (LlamaSharpModuleId, "local_models") =>
                InvokeLocalModelsAsync(operation, parameters, ct),
            _ => throw new NotSupportedException(
                $"Module storage '{moduleId}/{storageName}' is not registered."),
        };

    private async Task<JsonElement> InvokeScheduledJobsAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct)
    {
        var service = GetRequiredModuleService(
            "SharpClaw.Modules.AgentOrchestration.ScheduledJobs.IScheduledJobService");

        return operation switch
        {
            "create" => ToElement(await InvokeAsync(
                service,
                "CreateAsync",
                [Deserialize(parameters, "SharpClaw.Modules.AgentOrchestration.ScheduledJobs.CreateScheduledJobRequest"), ct])),
            "get" => ToElement(await InvokeAsync(
                service,
                "GetByIdAsync",
                [ReadId(parameters), ct])),
            "list" => ToElement(await InvokeAsync(service, "ListAsync", [ct])),
            "update" => ToElement(await InvokeAsync(
                service,
                "UpdateAsync",
                [
                    ReadId(parameters),
                    DeserializeRequiredProperty(
                        parameters,
                        "request",
                        "SharpClaw.Modules.AgentOrchestration.ScheduledJobs.UpdateScheduledJobRequest"),
                    ct,
                ])),
            "delete" => ToElement(await InvokeAsync(
                service,
                "DeleteAsync",
                [ReadId(parameters), ct])),
            "pause" => ToElement(await InvokeAsync(
                service,
                "PauseAsync",
                [ReadId(parameters), ct])),
            "resume" => ToElement(await InvokeAsync(
                service,
                "ResumeAsync",
                [ReadId(parameters), ct])),
            "preview_job" => ToElement(await InvokeAsync(
                service,
                "PreviewJobAsync",
                [ReadId(parameters), ReadInt(parameters, "count", 10), ct])),
            "preview_expression" => ToElement(Invoke(
                service,
                "PreviewExpression",
                [
                    ReadString(parameters, "expression", required: true)!,
                    ReadString(parameters, "timezone"),
                    ReadInt(parameters, "count", 10),
                ])),
            "list_ids" => ToElement(SelectListProperties(
                await InvokeAsync(service, "ListAsync", [ct]),
                item => GetRequiredProperty<Guid>(item, "Id"))),
            "lookup_items" => ToElement(SelectListProperties(
                await InvokeAsync(service, "ListAsync", [ct]),
                item => new ModuleStorageLookupItem(
                    GetRequiredProperty<Guid>(item, "Id"),
                    GetRequiredProperty<string>(item, "Name")))),
            _ => throw UnknownOperation("scheduled_jobs", operation),
        };
    }

    private async Task<JsonElement> InvokeSkillsAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct)
    {
        var db = GetRequiredModuleDbContext("SharpClaw.Modules.AgentOrchestration.AgentOrchestrationDbContext");
        var skillType = GetRequiredModuleType("SharpClaw.Modules.AgentOrchestration.Models.SkillDB");

        return operation switch
        {
            "create" => ToElement(await CreateSkillAsync(db, skillType, parameters, ct)),
            "get" => ToElement(await GetSkillAsync(db, skillType, ReadId(parameters), ct)),
            "list" => ToElement(await ListSkillsAsync(db, skillType, ct)),
            "update" => ToElement(await UpdateSkillAsync(db, skillType, parameters, ct)),
            "delete" => ToElement(await DeleteSkillAsync(db, skillType, ReadId(parameters), ct)),
            "access" => ToElement(await AccessSkillAsync(db, skillType, ReadId(parameters), ct)),
            "list_ids" => ToElement((await ListSkillsAsync(db, skillType, ct))
                .Select(skill => skill.Id)
                .ToList()),
            "lookup_items" => ToElement((await ListSkillsAsync(db, skillType, ct))
                .Select(skill => new ModuleStorageLookupItem(skill.Id, skill.Name))
                .ToList()),
            _ => throw UnknownOperation("skills", operation),
        };
    }

    private async Task<JsonElement> InvokeEditorSessionsAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct)
    {
        var service = GetRequiredModuleService(
            "SharpClaw.Modules.EditorCommon.Services.EditorSessionService");

        return operation switch
        {
            "create" => ToElement(await InvokeAsync(
                service,
                "CreateAsync",
                [Deserialize<CreateEditorSessionRequest>(parameters), ct])),
            "get" => ToElement(await InvokeAsync(
                service,
                "GetByIdAsync",
                [ReadId(parameters), ct])),
            "list" => ToElement(await InvokeAsync(service, "ListAsync", [ct])),
            "update" => ToElement(await InvokeAsync(
                service,
                "UpdateAsync",
                [
                    ReadId(parameters),
                    DeserializeRequiredProperty<UpdateEditorSessionRequest>(parameters, "request"),
                    ct,
                ])),
            "delete" => ToElement(await InvokeAsync(
                service,
                "DeleteAsync",
                [ReadId(parameters), ct])),
            "active_connections" => ToElement(ReadActiveEditorConnections()),
            "list_ids" => ToElement(SelectListProperties(
                await InvokeAsync(service, "ListAsync", [ct]),
                item => GetRequiredProperty<Guid>(item, "Id"))),
            "lookup_items" => ToElement(SelectListProperties(
                await InvokeAsync(service, "ListAsync", [ct]),
                item => new ModuleStorageLookupItem(
                    GetRequiredProperty<Guid>(item, "Id"),
                    GetRequiredProperty<string>(item, "Name")))),
            _ => throw UnknownOperation("editor_sessions", operation),
        };
    }

    private async Task<JsonElement> InvokeLocalModelsAsync(
        string operation,
        JsonElement parameters,
        CancellationToken ct)
    {
        var service = GetRequiredModuleService(
            "SharpClaw.Modules.Providers.LlamaSharp.Services.LocalModelService");

        return operation switch
        {
            "list" => ToElement(await InvokeAsync(service, "ListLocalModelsAsync", [ct])),
            "delete" => ToElement(await InvokeAsync(
                service,
                "DeleteLocalModelAsync",
                [ReadId(parameters, "modelId"), ct])),
            "set_mmproj" => ToElement(await SetMmprojPathAsync(service, parameters, ct)),
            "list_available_files" => ToElement(await InvokeAsync(
                service,
                "ListAvailableFilesAsync",
                [ReadString(parameters, "url", required: true)!, ct])),
            "ready_file_path" => ToElement(await InvokeAsync(
                GetRequiredModuleService(
                    "SharpClaw.Modules.Providers.LlamaSharp.Services.ILocalModelFileLookup"),
                "GetReadyFilePathAsync",
                [ReadId(parameters, "modelId"), ct])),
            "source_url" => ToElement(await InvokeAsync(
                GetRequiredModuleService(
                    "SharpClaw.Modules.Providers.LlamaSharp.Services.LocalModelLookup"),
                "GetSourceUrlAsync",
                [ReadId(parameters, "modelId"), ct])),
            _ => throw UnknownOperation("local_models", operation),
        };
    }

    private async Task<SkillStorageResponse> CreateSkillAsync(
        DbContext db,
        Type skillType,
        JsonElement parameters,
        CancellationToken ct)
    {
        var request = Deserialize<CreateSkillStorageRequest>(parameters);
        var skill = Activator.CreateInstance(skillType)
            ?? throw new InvalidOperationException($"Could not create '{skillType.FullName}'.");

        SetProperty(skill, "Name", request.Name);
        SetProperty(skill, "Description", request.Description);
        SetProperty(skill, "SkillText", request.SkillText);

        db.Add(skill);
        await db.SaveChangesAsync(ct);
        return ToSkillResponse(skill);
    }

    private async Task<SkillStorageResponse?> GetSkillAsync(
        DbContext db,
        Type skillType,
        Guid id,
        CancellationToken ct)
    {
        var skill = await db.FindAsync(skillType, [id], ct);
        return skill is null ? null : ToSkillResponse(skill);
    }

    private async Task<IReadOnlyList<SkillStorageResponse>> ListSkillsAsync(
        DbContext db,
        Type skillType,
        CancellationToken ct)
    {
        var skills = await ListEntitiesAsync(db, skillType, ct);
        return [.. skills
            .OrderBy(skill => GetRequiredProperty<string>(skill, "Name"), StringComparer.Ordinal)
            .Select(ToSkillResponse)];
    }

    private async Task<SkillStorageResponse?> UpdateSkillAsync(
        DbContext db,
        Type skillType,
        JsonElement parameters,
        CancellationToken ct)
    {
        var request = Deserialize<UpdateSkillStorageRequest>(parameters);
        var skill = await db.FindAsync(skillType, [request.Id], ct);
        if (skill is null)
            return null;

        if (request.Name is not null)
            SetProperty(skill, "Name", request.Name);
        if (request.Description is not null)
            SetProperty(skill, "Description", request.Description);
        if (request.SkillText is not null)
            SetProperty(skill, "SkillText", request.SkillText);

        SetProperty(skill, "UpdatedAt", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return ToSkillResponse(skill);
    }

    private async Task<bool> DeleteSkillAsync(
        DbContext db,
        Type skillType,
        Guid id,
        CancellationToken ct)
    {
        var skill = await db.FindAsync(skillType, [id], ct);
        if (skill is null)
            return false;

        db.Remove(skill);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string?> AccessSkillAsync(
        DbContext db,
        Type skillType,
        Guid id,
        CancellationToken ct)
    {
        var skill = await db.FindAsync(skillType, [id], ct);
        return skill is null
            ? null
            : $"Skill: {GetRequiredProperty<string>(skill, "Name")}\n\n"
              + GetRequiredProperty<string>(skill, "SkillText");
    }

    private static async Task<bool> SetMmprojPathAsync(
        object service,
        JsonElement parameters,
        CancellationToken ct)
    {
        await InvokeAsync(
            service,
            "SetMmprojPathAsync",
            [ReadId(parameters, "modelId"), ReadString(parameters, "mmprojPath"), ct]);
        return true;
    }

    private IReadOnlyList<EditorConnectionStorageResponse> ReadActiveEditorConnections()
    {
        var bridge = GetRequiredModuleService(
            "SharpClaw.Modules.EditorCommon.Services.EditorBridgeService");
        var connections = Invoke(bridge, "GetConnections", []);
        if (connections is not IEnumerable enumerable)
            return [];

        return [.. enumerable.Cast<object>().Select(connection =>
            new EditorConnectionStorageResponse(
                GetRequiredProperty<string>(connection, "ConnectionId"),
                GetRequiredProperty<Guid>(connection, "SessionId"),
                Convert.ToString(GetProperty(connection, "EditorType")) ?? string.Empty,
                GetProperty<string?>(connection, "EditorVersion"),
                GetProperty<string?>(connection, "WorkspacePath"),
                GetRequiredProperty<DateTimeOffset>(connection, "ConnectedAt")))];
    }

    private object GetRequiredModuleService(string typeName)
    {
        var type = GetRequiredModuleType(typeName);
        return services.GetRequiredService(type);
    }

    private DbContext GetRequiredModuleDbContext(string typeName)
    {
        var service = GetRequiredModuleService(typeName);
        return service as DbContext
            ?? throw new NotSupportedException($"Module service '{typeName}' is not an EF DbContext.");
    }

    private static Type GetRequiredModuleType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null)
        ?? throw new NotSupportedException($"Module type '{fullName}' is not loaded.");

    private static IReadOnlyList<object> SelectListProperties(
        object? list,
        Func<object, object> selector)
    {
        if (list is not IEnumerable enumerable)
            return [];

        return [.. enumerable.Cast<object>().Select(selector)];
    }

    private static async Task<IReadOnlyList<object>> ListEntitiesAsync(
        DbContext db,
        Type entityType,
        CancellationToken ct)
    {
        var set = CreateQueryableSet(db, entityType);
        var castMethod = typeof(Queryable)
            .GetMethod(nameof(Queryable.Cast), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(object));
        var cast = (IQueryable<object>)castMethod.Invoke(null, [set])!;
        return await cast.ToListAsync(ct);
    }

    private static IQueryable CreateQueryableSet(DbContext db, Type entityType)
    {
        var setMethod = typeof(DbContext).GetMethods()
            .Single(method => method.Name == nameof(DbContext.Set)
                              && method.IsGenericMethod
                              && method.GetParameters().Length == 0);
        return (IQueryable)setMethod.MakeGenericMethod(entityType).Invoke(db, [])!;
    }

    private static object? Invoke(object target, string methodName, object?[] args)
    {
        var method = FindMethod(target.GetType(), methodName, args.Length);
        return method.Invoke(target, args);
    }

    private static async Task<object?> InvokeAsync(object target, string methodName, object?[] args)
    {
        var result = Invoke(target, methodName, args);
        if (result is not Task task)
            return result;

        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }

    private static MethodInfo FindMethod(Type type, string methodName, int parameterCount) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()))
            .FirstOrDefault(method => method.Name == methodName
                                      && method.GetParameters().Length == parameterCount)
        ?? throw new NotSupportedException(
            $"Method '{type.FullName}.{methodName}' with {parameterCount} parameter(s) was not found.");

    private static IReadOnlyList<ModuleStorageOperationDescriptor> Operations(params string[] names) =>
        [.. names.Select(name => new ModuleStorageOperationDescriptor(name))];

    private static T Deserialize<T>(JsonElement parameters) =>
        parameters.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException($"Could not deserialize {typeof(T).Name}.");

    private static object Deserialize(JsonElement parameters, string typeName)
    {
        var type = GetRequiredModuleType(typeName);
        return JsonSerializer.Deserialize(parameters.GetRawText(), type, JsonOptions)
            ?? throw new ArgumentException($"Could not deserialize {type.Name}.");
    }

    private static T DeserializeRequiredProperty<T>(JsonElement parameters, string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var property))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return Deserialize<T>(property);
    }

    private static object DeserializeRequiredProperty(
        JsonElement parameters,
        string propertyName,
        string typeName)
    {
        if (!parameters.TryGetProperty(propertyName, out var property))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return Deserialize(property, typeName);
    }

    private static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private static Guid ReadId(JsonElement parameters, string propertyName = "id")
    {
        if (!parameters.TryGetProperty(propertyName, out var property))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        if (property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out var parsed))
            return parsed;

        if (property.ValueKind == JsonValueKind.Undefined
            || property.ValueKind == JsonValueKind.Null)
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return property.GetGuid();
    }

    private static int ReadInt(JsonElement parameters, string propertyName, int defaultValue) =>
        parameters.TryGetProperty(propertyName, out var property)
            ? property.GetInt32()
            : defaultValue;

    private static string? ReadString(
        JsonElement parameters,
        string propertyName,
        bool required = false)
    {
        if (!parameters.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            if (required)
                throw new ArgumentException($"Property '{propertyName}' is required.");
            return null;
        }

        var value = property.GetString();
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Property '{propertyName}' is required.");

        return value;
    }

    private static object? GetProperty(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source);

    private static T? GetProperty<T>(object source, string name) =>
        (T?)GetProperty(source, name);

    private static T GetRequiredProperty<T>(object source, string name) =>
        GetProperty<T>(source, name)
        ?? throw new InvalidOperationException(
            $"Property '{source.GetType().FullName}.{name}' was not found.");

    private static void SetProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name)
            ?? throw new InvalidOperationException(
                $"Property '{target.GetType().FullName}.{name}' was not found.");

        property.SetValue(target, value);
    }

    private static NotSupportedException UnknownOperation(string storageName, string operation) =>
        new($"Storage operation '{storageName}.{operation}' is not registered.");

    private static SkillStorageResponse ToSkillResponse(object skill) =>
        new(
            GetRequiredProperty<Guid>(skill, "Id"),
            GetRequiredProperty<string>(skill, "Name"),
            GetProperty<string?>(skill, "Description"),
            GetRequiredProperty<string>(skill, "SkillText"),
            GetRequiredProperty<DateTimeOffset>(skill, "CreatedAt"),
            GetRequiredProperty<DateTimeOffset>(skill, "UpdatedAt"));

    private sealed record CreateSkillStorageRequest(
        string Name,
        string SkillText,
        string? Description = null);

    private sealed record UpdateSkillStorageRequest(
        Guid Id,
        string? Name = null,
        string? SkillText = null,
        string? Description = null);

    private sealed record SkillStorageResponse(
        Guid Id,
        string Name,
        string? Description,
        string SkillText,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ModuleStorageLookupItem(Guid Id, string Name);

    private sealed record EditorConnectionStorageResponse(
        string ConnectionId,
        Guid SessionId,
        string EditorKey,
        string? EditorVersion,
        string? WorkspacePath,
        DateTimeOffset ConnectedAt);
}
