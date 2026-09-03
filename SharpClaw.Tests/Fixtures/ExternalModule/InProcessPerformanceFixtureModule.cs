using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class InProcessPerformanceFixtureModule : ISharpClawModule
{
    public const string ModuleId = "synthetic_inprocess_perf";
    public const string ToolPrefixValue = "sip";
    public const string NoopTool = "synthetic_inprocess_perf_noop";
    public const string StorageTool = "synthetic_inprocess_perf_storage";
    public const string SpawnJobTool = "synthetic_inprocess_perf_spawn_job";
    public const string StorageName = "records";

    public ModuleIdentity Identity { get; } = new(ModuleId, "Synthetic in-process performance", ToolPrefixValue);

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddSingleton<InProcessPerformanceToolHandler>();
        module.Storage.Add(new ModuleStorageContractDescriptor(
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
            MaxBatchSize: 100));
        foreach (var tool in new[] { NoopTool, StorageTool, SpawnJobTool })
            module.Tools.Add<InProcessPerformanceToolHandler>(
                new ToolDescriptor(tool, "Synthetic in-process performance tool.", ToolSchemas.EmptyObject));
    }
}

internal sealed class InProcessPerformanceToolHandler : IToolHandler
{
    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToolResult.Text($"synthetic:{invocation.ToolName}"));
    }
}
