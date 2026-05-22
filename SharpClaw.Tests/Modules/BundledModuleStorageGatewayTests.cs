using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledModuleStorageGatewayTests
{
    [Test]
    public void BundledModuleStorageGatewayListsCurrentParentBackedStorageContracts()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var gateway = new BundledModuleStorageGateway(services);

        var contracts = gateway.ListContracts();

        contracts.Select(contract => $"{contract.ModuleId}/{contract.StorageName}")
            .Should()
            .BeEquivalentTo(
            [
                "sharpclaw_agent_orchestration/scheduled_jobs",
                "sharpclaw_agent_orchestration/skills",
                "sharpclaw_editor_common/editor_sessions",
                "sharpclaw_providers_llamasharp/local_models",
            ]);

        contracts.Single(contract => contract.StorageName == "scheduled_jobs")
            .Operations.Select(operation => operation.Name)
            .Should()
            .Contain(["create", "list", "pause", "resume", "lookup_items"]);

        contracts.Single(contract => contract.StorageName == "local_models")
            .Operations.Select(operation => operation.Name)
            .Should()
            .Contain(["list", "ready_file_path", "source_url"]);
    }
}
