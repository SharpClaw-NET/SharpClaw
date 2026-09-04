using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class InProcessPerformanceFixtureRegistration : ISharpClawModule
{
    public const string SourceId = "synthetic_inprocess_perf";
    public const string ToolPrefixValue = "sip";
    public const string NoopTool = "synthetic_inprocess_perf_noop";
    public const string StorageTool = "synthetic_inprocess_perf_storage";
    public const string SpawnJobTool = "synthetic_inprocess_perf_spawn_job";
    public const string StorageName = "records";

    public ModuleIdentity Identity { get; } = new(SourceId, "Synthetic in-process performance", ToolPrefixValue);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<InProcessPerformanceToolHandler>();
        services.AddStorage(new ScopedStorageContractDescriptor(
            SourceId,
            StorageName,
            [
                new(ScopedStorageOperations.Get),
                new(ScopedStorageOperations.Upsert),
                new(ScopedStorageOperations.BatchUpsert),
                new(ScopedStorageOperations.Delete),
                new(ScopedStorageOperations.BatchDelete),
                new(ScopedStorageOperations.List),
                new(ScopedStorageOperations.Query),
                new(ScopedStorageOperations.Claim),
            ],
            Indexes:
            [
                new("status", ScopedStorageIndexValueKind.String),
                new("bucket", ScopedStorageIndexValueKind.Number, AllowsRange: true),
                new("priority", ScopedStorageIndexValueKind.Number, AllowsRange: true),
                new("nextRunAt", ScopedStorageIndexValueKind.DateTime, AllowsRange: true),
            ],
            MaxDocumentBytes: 65_536,
            MaxBatchSize: 100));
        foreach (var tool in new[] { NoopTool, StorageTool, SpawnJobTool })
            services.AddTool<InProcessPerformanceToolHandler>(
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
