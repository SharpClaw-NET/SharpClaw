using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class InProcessStorageFixtureModule : ISharpClawCoreModule
{
    public const string ModuleId = "synthetic_inprocess_storage";
    public const string ToolPrefixValue = "sis";
    public const string StorageName = "records";

    public string Id => ModuleId;
    public string DisplayName => "Synthetic In-Process Storage";
    public string ToolPrefix => ToolPrefixValue;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModuleStorageGateway, FakeModuleStorageGateway>();
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() =>
    [
        new(
            ModuleId,
            StorageName,
            [
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Upsert),
            ],
            Indexes:
            [
                new("name", ModuleStorageIndexValueKind.String),
            ])
    ];

    public Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        Task.FromResult("synthetic in-process storage");

    private sealed class FakeModuleStorageGateway : IModuleStorageGateway
    {
        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Module-owned fake storage gateway should be replaced.");
    }
}
