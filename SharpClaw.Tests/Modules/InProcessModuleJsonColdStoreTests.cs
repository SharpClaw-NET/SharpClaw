using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules;
using SharpClaw.Tests.TestHarness;
using SharpClaw.TestFixtures.ExternalRegistration;

namespace SharpClaw.Tests.Modules;

[TestFixture]
[NonParallelizable]
public sealed class InProcessRegistrationJsonColdStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task InProcessRegistration_StorageClaimAndSpawnJob_WorkWithJsonColdStore()
    {
        await using var host = ChatHarnessHost.Create(
            new Dictionary<string, string?>
            {
                ["Packages:DotNetHostingMode"] = "allow-in-process",
            },
            useJsonColdStoreDatabase: true);
        var module = new InProcessPerformanceFixtureRegistration();
        var registry = host.RootServices.GetRequiredService<RegistrationCatalog>();
        registry.Register(module);
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            disableToolSchemas: true);

        using var scope = host.CreateScope();
        var restrictedScope = RegistrationHostServiceAccess.CreateRestrictedScope(
            scope.ServiceProvider,
            module.Id);
        var job = new AgentJobContext(
            Guid.NewGuid(),
            seeded.Agent.Id,
            seeded.Channel.Id,
            ResourceId: null,
            ActionKey: InProcessPerformanceFixtureRegistration.NoopTool);

        using var storageParameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { variant = 900 }, JsonOptions));
        var storageResult = await module.ExecuteToolAsync(
            InProcessPerformanceFixtureRegistration.StorageTool,
            storageParameters.RootElement,
            job,
            restrictedScope,
            CancellationToken.None);

        storageResult.Should().Be("storage:900:1:1");

        using var spawnParameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { variant = 901 }, JsonOptions));
        var spawnResult = await module.ExecuteToolAsync(
            InProcessPerformanceFixtureRegistration.SpawnJobTool,
            spawnParameters.RootElement,
            job,
            restrictedScope,
            CancellationToken.None);

        spawnResult.Should().StartWith("spawn:901:");
    }
}
