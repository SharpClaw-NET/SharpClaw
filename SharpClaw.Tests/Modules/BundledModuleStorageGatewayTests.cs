using SharpClaw.Application.API;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledModuleStorageGatewayTests
{
    [Test]
    public void BundledModuleStorageGatewayHasNoParentBackedStorageContracts()
    {
        var gateway = new BundledModuleStorageGateway();

        var contracts = gateway.ListContracts();

        contracts.Should().BeEmpty();
    }
}
