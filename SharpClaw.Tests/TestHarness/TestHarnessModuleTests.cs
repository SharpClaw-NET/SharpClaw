using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Core.Clients;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessModuleTests
{
    [Test]
    public async Task ModuleRegistersDynamicProvidersToolsHeaderTagsAndCostFeed()
    {
        await using var host = ChatHarnessHost.Create();

        var factory = host.Services.GetRequiredService<ProviderApiClientFactory>();
        factory.IsAvailable(TestHarnessConstants.PlainProviderKey).Should().BeTrue();
        factory.IsAvailable(TestHarnessConstants.StreamingProviderKey).Should().BeTrue();
        factory.IsAvailable(TestHarnessConstants.ToolProviderKey).Should().BeTrue();
        factory.IsAvailable(TestHarnessConstants.CostProviderKey).Should().BeTrue();
        factory.IsAvailable(TestHarnessConstants.EdenStyleProviderKey).Should().BeTrue();

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        registry.GetHeaderTag(TestHarnessConstants.HeaderTagName).Should().NotBeNull();
        registry.IsInlineTool(TestHarnessConstants.InlinePermissionedTool).Should().BeTrue();
        registry.IsInlineTool(TestHarnessConstants.InlinePermissionedToolAlias).Should().BeTrue();
        registry.TryResolve(TestHarnessConstants.JobPermissionedTool, out var moduleId, out var toolName)
            .Should().BeTrue();
        moduleId.Should().Be(TestHarnessConstants.ModuleId);
        toolName.Should().Be(TestHarnessConstants.JobPermissionedTool);
        registry.GetDescriptorByDefaultResourceKey(TestHarnessConstants.DefaultResourceKey)
            .Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ProviderCaptureRedactsSecretsButKeepsAssertablePromptShape()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "system token=secret-token-value");
        seeded.Provider.EncryptedApiKey = "sk-real-secret123456789";
        using var providerParams = JsonDocument.Parse("""{"api_key":"sk-secret123456789","safe":"visible"}""");
        seeded.Agent.ProviderParameters = new Dictionary<string, JsonElement>
        {
            ["api_key"] = providerParams.RootElement.GetProperty("api_key").Clone(),
            ["safe"] = providerParams.RootElement.GetProperty("safe").Clone()
        };
        await host.Db.SaveChangesAsync();

        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("hello api_key=abc123 and sk-user-secret123456789"));

        var request = host.Harness.ProviderRequests.Single();
        request.ApiKeyFingerprint.Should().NotBe("sk-real-secret123456789");
        request.SystemPrompt.Should().Contain("[redacted]");
        request.Messages.Single().Content.Should().Contain("[redacted]");
        request.ProviderParameters["api_key"].Should().Be("[redacted]");
        request.ProviderParameters["safe"].Should().Contain("visible");
    }

    [Test]
    public async Task CostFeedUsesHarnessBehaviorAndRecordsTiming()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigureCost(new TestHarnessCostBehavior
        {
            LatencyMs = 25,
            Result = new ProviderCostResult(
                4.50m,
                "usd",
                [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), 4.50m)])
        });

        var plugin = host.Services.GetRequiredService<ProviderApiClientFactory>()
            .GetPlugin(TestHarnessConstants.CostProviderKey);

        plugin!.SupportsCostFeed.Should().BeTrue();
        var costFeed = plugin.CreateCostFeed(new ProviderClientOptions(null));
        costFeed.Should().NotBeNull();
        var result = await costFeed!.GetCostsAsync(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1));

        result!.TotalAmount.Should().Be(4.50m);
        host.Harness.CostCalls.Single().ElapsedMs.Should().BeGreaterThanOrEqualTo(20);
    }

    [TestCaseSource(nameof(ProviderMatrixBatches))]
    public async Task ProviderMatrixScalesPastOneThousandDeterministicCases(int start, int count)
    {
        await using var host = ChatHarnessHost.Create();
        var client = host.Services.GetRequiredService<ProviderApiClientFactory>()
            .GetClient(
                TestHarnessConstants.PlainProviderKey,
                new ProviderClientOptions(null));

        for (var i = start; i < start + count; i++)
        {
            host.Harness.ConfigureProvider(
                TestHarnessConstants.PlainProviderKey,
                new TestHarnessProviderScenario
                {
                    Turns =
                    [
                        new TestHarnessProviderTurn
                        {
                            Content = $"case-{i:D4}",
                            Usage = new TokenUsage(i % 17, i % 13)
                        }
                    ]
                });

            var result = await client.ChatCompletionAsync(
                TestHarnessConstants.ModelId,
                "system",
                [new ChatCompletionMessage("user", $"message-{i:D4}")]);

            result.Content.Should().Be($"case-{i:D4}");
            result.Usage!.PromptTokens.Should().Be(i % 17);
            result.Usage.CompletionTokens.Should().Be(i % 13);
        }
    }

    private static IEnumerable<TestCaseData> ProviderMatrixBatches()
    {
        for (var batch = 0; batch < 16; batch++)
            yield return new TestCaseData(batch * 64, 64).SetName($"ProviderMatrixBatch_{batch:D2}");
    }
}
