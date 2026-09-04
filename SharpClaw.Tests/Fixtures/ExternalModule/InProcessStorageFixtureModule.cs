using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class InProcessStorageFixtureRegistration : ISharpClawModule
{
    public const string SourceId = "synthetic_inprocess_storage";
    public const string ToolPrefixValue = "sis";
    public const string StorageName = "records";

    public ModuleIdentity Identity { get; } = new(SourceId, "Synthetic in-process storage", ToolPrefixValue);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<InProcessStorageToolHandler>();
        services.AddStorage(new ScopedStorageContractDescriptor(
            SourceId,
            StorageName,
            [
                new(ScopedStorageOperations.List),
                new(ScopedStorageOperations.Upsert),
            ],
            Indexes: [new("name", ScopedStorageIndexValueKind.String)]));
        services.AddTool<InProcessStorageToolHandler>(
            new ToolDescriptor("synthetic_inprocess_storage", "Synthetic storage tool.", ToolSchemas.EmptyObject));
    }
}

internal sealed class InProcessStorageToolHandler : IToolHandler
{
    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToolResult.Text("synthetic in-process storage"));
    }
}
