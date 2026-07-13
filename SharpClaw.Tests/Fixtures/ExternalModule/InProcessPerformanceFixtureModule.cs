using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class InProcessPerformanceFixtureModule : ISharpClawCoreModule
{
    public const string ModuleId = "synthetic_inprocess_perf";
    public const string ToolPrefixValue = "sip";
    public const string NoopTool = "synthetic_inprocess_perf_noop";
    public const string StorageTool = "synthetic_inprocess_perf_storage";
    public const string SpawnJobTool = "synthetic_inprocess_perf_spawn_job";
    public const string StorageName = "records";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id => ModuleId;
    public string DisplayName => "Synthetic In-Process Performance";
    public string ToolPrefix => ToolPrefixValue;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var permission = new ModuleToolPermission(
            IsPerResource: false,
            Check: (_, _, _, _) => Task.FromResult(
                AgentActionResult.Approve(
                    "Synthetic in-process performance tool approved.",
                    PermissionClearance.Independent)));

        return
        [
            new(NoopTool, "No-op in-process dispatch performance tool.", EmptySchema(), permission),
            new(StorageTool, "Storage CRUD/query/claim in-process performance tool.", EmptySchema(), permission),
            new(SpawnJobTool, "Host job-controller submission performance tool.", EmptySchema(), permission),
        ];
    }

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() =>
    [
        new(
            ModuleId,
            StorageName,
            [
                new(ModuleStorageOperations.Get),
                new(ModuleStorageOperations.Upsert),
                new(ModuleStorageOperations.BatchUpsert),
                new(ModuleStorageOperations.Delete),
                new(ModuleStorageOperations.BatchDelete),
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Query),
                new(ModuleStorageOperations.Claim),
            ],
            Indexes:
            [
                new("status", ModuleStorageIndexValueKind.String),
                new("bucket", ModuleStorageIndexValueKind.Number, AllowsRange: true),
                new("priority", ModuleStorageIndexValueKind.Number, AllowsRange: true),
                new("nextRunAt", ModuleStorageIndexValueKind.DateTime, AllowsRange: true),
            ],
            MaxDocumentBytes: 65_536,
            MaxBatchSize: 100),
    ];

    public async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        return toolName switch
        {
            NoopTool => ExecuteNoop(parameters, scopedServices),
            StorageTool => await ExecuteStorageAsync(parameters, job, scopedServices, ct),
            SpawnJobTool => await ExecuteSpawnJobAsync(parameters, job, scopedServices, ct),
            _ => throw new InvalidOperationException($"Unknown in-process performance tool: '{toolName}'."),
        };
    }

    private static string ExecuteNoop(JsonElement parameters, IServiceProvider scopedServices)
    {
        var core = scopedServices.GetRequiredService<ISharpClawDataContext>();
        var agentProbe = core.Agents.Take(1).Count();
        var variant = ReadInt(parameters, "variant");
        return $"noop:{variant}:{agentProbe}";
    }

    private static async Task<string> ExecuteStorageAsync(
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var variant = ReadInt(parameters, "variant");
        var gateway = scopedServices.GetRequiredService<IModuleStorageGateway>();
        var store = new ModuleDocumentStore<PerfRecord>(
            gateway,
            ModuleId,
            StorageName,
            JsonOptions);
        var key = $"case-{variant:D3}-{job.JobId:N}";
        var nextRunAt = DateTimeOffset.UnixEpoch.AddMinutes(variant);
        var pending = new PerfRecord(key, "Pending", variant, variant % 10, nextRunAt);

        await store.UpsertAsync(
            key,
            pending,
            new
            {
                status = pending.Status,
                bucket = pending.Bucket,
                priority = pending.Priority,
                nextRunAt = pending.NextRunAt,
            },
            ct);

        var queried = await store.Query()
            .WhereIndex("bucket").EqualTo(variant)
            .OrderByIndex("nextRunAt")
            .Take(1)
            .ToListAsync(ct);

        var claimed = await store.Claim()
            .WhereIndex("bucket").EqualTo(variant)
            .OrderByIndex("nextRunAt")
            .Take(1)
            .Patch(
                new { status = "Running" },
                new { status = "Running" })
            .ToListAsync(ct);

        return $"storage:{variant}:{queried.Count}:{claimed.Count}";
    }

    private static async Task<string> ExecuteSpawnJobAsync(
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var variant = ReadInt(parameters, "variant");
        var jobs = scopedServices.GetRequiredService<IAgentJobController>();
        var response = await jobs.SubmitJobAsync(
            job.ChannelId,
            new SubmitAgentJobRequest(
                ActionKey: NoopTool,
                ScriptJson: JsonSerializer.Serialize(new { variant, source = "spawn" }, JsonOptions)),
            ct);

        return $"spawn:{variant}:{response.Status}:{response.ResultData}";
    }

    private static int ReadInt(JsonElement parameters, string propertyName) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """);
        return doc.RootElement.Clone();
    }

    private sealed record PerfRecord(
        string Key,
        string Status,
        int Bucket,
        int Priority,
        DateTimeOffset NextRunAt);
}
