using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Clients;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Tests.TestHarness;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessProviderRegistrationTests
{
    [Test]
    public async Task DynamicModuleRegisteredProvidersAppearInPickerService()
    {
        await using var host = ChatHarnessHost.Create();

        var types = host.Services.GetRequiredService<ProviderService>().ListAvailableTypes();

        types.Select(t => t.ProviderKey).Should().Contain(
        [
            TestHarnessConstants.PlainProviderKey,
            TestHarnessConstants.StreamingProviderKey,
            TestHarnessConstants.ToolProviderKey,
            TestHarnessConstants.EdenStyleProviderKey
        ]);
    }

    [Test]
    public async Task DisabledProviderModuleIsNotSelectable()
    {
        await using var host = ChatHarnessHost.Create();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var factory = host.Services.GetRequiredService<ProviderApiClientFactory>();

        registry.Unregister(TestHarnessConstants.ModuleId);

        factory.IsAvailable(TestHarnessConstants.PlainProviderKey).Should().BeFalse();
        host.Services.GetRequiredService<ProviderService>()
            .ListAvailableTypes()
            .Select(t => t.ProviderKey)
            .Should().NotContain(TestHarnessConstants.PlainProviderKey);
    }

    [Test]
    public async Task EdenAiStyleProviderRegistrationUsesSameDynamicPath()
    {
        await using var host = ChatHarnessHost.Create();
        var providerService = host.Services.GetRequiredService<ProviderService>();
        var provider = await providerService.CreateAsync(new CreateProviderRequest(
            "Harness Eden",
            TestHarnessConstants.EdenStyleProviderKey,
            ApiKey: null,
            ApiEndpoint: null));

        var models = await providerService.SyncModelsAsync(provider.Id);

        models.Select(m => m.Name).Should().Contain("edenai/harness-chat");
    }

    [Test]
    public async Task ProviderParametersArePassedAndSanitized()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        using var doc = JsonDocument.Parse("""{"safe":"visible","api_key":"sk-secret123456789"}""");
        seeded.Agent.ProviderParameters = new Dictionary<string, JsonElement>
        {
            ["safe"] = doc.RootElement.GetProperty("safe").Clone(),
            ["api_key"] = doc.RootElement.GetProperty("api_key").Clone()
        };
        await host.Db.SaveChangesAsync();

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("params"));

        var request = host.Harness.ProviderRequests.Single();
        request.ProviderParameters["safe"].Should().Contain("visible");
        request.ProviderParameters["api_key"].Should().Be("[redacted]");
    }

    [Test]
    public async Task CustomHeadersAreNotTreatedAsProviderIdentity()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            customHeader: $"provider={TestHarnessConstants.EdenStyleProviderKey}\n");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("identity"));

        var request = host.Harness.ProviderRequests.Single();
        request.ProviderKey.Should().Be(TestHarnessConstants.PlainProviderKey);
        request.Messages.Single(m => m.Role == "user")
            .Content.Should().Contain(TestHarnessConstants.EdenStyleProviderKey);
    }
}
