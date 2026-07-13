using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;
using SharpClaw.Tests.TestHarness;
using SharpClaw.TestFixtures.ExternalModule;

namespace SharpClaw.Tests.Modules;

[TestFixture]
[NonParallelizable]
public sealed class InProcessModuleJsonColdStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task InProcessModule_StorageClaimAndSpawnJob_WorkWithJsonColdStore()
    {
        await using var host = ChatHarnessHost.Create(
            new Dictionary<string, string?>
            {
                ["Modules:DotNetHostingMode"] = "allow-in-process",
            },
            useJsonColdStoreDatabase: true);
        var module = new InProcessPerformanceFixtureModule();
        var registry = host.RootServices.GetRequiredService<ModuleRegistry>();
        registry.Register(module);
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            disableToolSchemas: true);

        using var scope = host.CreateScope();
        var restrictedScope = ModuleHostServiceAccess.CreateRestrictedScope(
            scope.ServiceProvider,
            module.Id);
        var job = new AgentJobContext(
            Guid.NewGuid(),
            seeded.Agent.Id,
            seeded.Channel.Id,
            ResourceId: null,
            ActionKey: InProcessPerformanceFixtureModule.NoopTool);

        using var storageParameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { variant = 900 }, JsonOptions));
        var storageResult = await module.ExecuteToolAsync(
            InProcessPerformanceFixtureModule.StorageTool,
            storageParameters.RootElement,
            job,
            restrictedScope,
            CancellationToken.None);

        storageResult.Should().Be("storage:900:1:1");

        using var spawnParameters = JsonDocument.Parse(
            JsonSerializer.Serialize(new { variant = 901 }, JsonOptions));
        var spawnResult = await module.ExecuteToolAsync(
            InProcessPerformanceFixtureModule.SpawnJobTool,
            spawnParameters.RootElement,
            job,
            restrictedScope,
            CancellationToken.None);

        spawnResult.Should().StartWith("spawn:901:");
    }
}
