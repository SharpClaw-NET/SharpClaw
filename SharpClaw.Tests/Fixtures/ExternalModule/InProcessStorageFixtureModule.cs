using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class InProcessStorageFixtureModule : ISharpClawModule
{
    public const string ModuleId = "synthetic_inprocess_storage";
    public const string ToolPrefixValue = "sis";
    public const string StorageName = "records";

    public ModuleIdentity Identity { get; } = new(ModuleId, "Synthetic in-process storage", ToolPrefixValue);

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddSingleton<InProcessStorageToolHandler>();
        module.Storage.Add(new ModuleStorageContractDescriptor(
            ModuleId,
            StorageName,
            [
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Upsert),
            ],
            Indexes: [new("name", ModuleStorageIndexValueKind.String)]));
        module.Tools.Add<InProcessStorageToolHandler>(
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
