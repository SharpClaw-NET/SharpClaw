using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessPromptAssemblyExpandedTests
{
    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task DefaultHeadersCanBeEnabledOrDisabled(
        bool disableDefaultHeaders,
        bool expectDefaultHeader)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = disableDefaultHeaders.ToString()
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("hello"));

        var content = host.Harness.ProviderRequests.Single()
            .Messages.Single(m => m.Role == "user")
            .Content!;
        content.StartsWith("[time:", StringComparison.Ordinal).Should().Be(expectDefaultHeader);
        content.EndsWith("hello", StringComparison.Ordinal).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DefaultSystemPromptCanBeEnabledOrDisabled(bool disableDefaultSystemPrompt)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultSystemPrompt"] = disableDefaultSystemPrompt.ToString()
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("system prompt"));

        var prompt = host.Harness.ProviderRequests.Single().SystemPrompt!;
        if (disableDefaultSystemPrompt)
        {
            prompt.Should().Be("agent system");
        }
        else
        {
            prompt.Should().StartWith("agent system");
            prompt.Length.Should().BeGreaterThan("agent system".Length);
        }
    }

    [Test]
    public async Task CustomHeaderLiteralBehaviorWhenHeaderExpansionIsDisabled()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableHeaderTagExpansion"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            customHeader: "literal {{testharness}} {{agent-name}}\n");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("body"));

        var content = host.Harness.ProviderRequests.Single()
            .Messages.Single(m => m.Role == "user")
            .Content;
        content.Should().Be("literal {{testharness}} {{agent-name}}\nbody");
        host.Harness.HeaderTagCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ModuleHeaderTagsDoNotExecuteWhenModuleTagsAreDisabled()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableModuleHeaderTags"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            customHeader: "module={{testharness}} agent={{agent-name}}\n");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("body"));

        var content = host.Harness.ProviderRequests.Single()
            .Messages.Single(m => m.Role == "user")
            .Content;
        content.Should().Be("module= agent=Harness Agent\nbody");
        host.Harness.HeaderTagCalls.Should().BeEmpty();
    }

    [Test]
    public async Task DynamicChatFeaturesDisabledSendOnlyPlainAgentSystemAndUserText()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true",
            ["Chat:CacheMaxMegabytes"] = "0"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            agentSystemPrompt: "plain agent",
            customHeader: null,
            disableToolSchemas: true);

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("plain user"));

        var request = host.Harness.ProviderRequests.Single();
        request.SystemPrompt.Should().Be("plain agent");
        request.Messages.Select(m => (m.Role, m.Content)).Should().Equal(
            [("user", "plain user")]);
        request.Tools.Should().BeEmpty();
    }

    [Test]
    public async Task PromptPartsAndToolDefinitionsArriveInStableOrder()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigureHeaderTag(new TestHarnessHeaderTagBehavior { Value = "module-tag" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            customHeader:
                "agent={{agent-name}}|channels={{Channels:{Title}}}|module={{testharness}}\n");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("stable order"));

        var request = host.Harness.ProviderRequests.Single();
        request.Messages.Single(m => m.Role == "user").Content.Should().Be(
            "agent=Harness Agent|channels=Harness Channel|module=module-tag\nstable order");
        request.SystemPrompt.Should().StartWith("agent system");
        request.Tools.Select(t => t.Name).Should().Equal(
        [
            TestHarnessConstants.JobPermissionedToolAlias,
            TestHarnessConstants.JobResourceTool,
            TestHarnessConstants.JobStreamingTool,
            TestHarnessConstants.InlineOpenTool,
            TestHarnessConstants.InlinePermissionedToolAlias
        ]);
    }

    [Test]
    public async Task PromptAndHeaderCacheInvalidatesAfterAgentAndChannelMutation()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "prompt-v1",
            customHeader: "h1 {{testharness}}\n");
        host.Harness.ConfigureHeaderTag(new TestHarnessHeaderTagBehavior { Value = "tag-v1" });

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("first"));

        await host.Services.GetRequiredService<AgentService>().UpdateAsync(
            seeded.Agent.Id,
            new UpdateAgentRequest(SystemPrompt: "prompt-v2"));
        await host.Services.GetRequiredService<ChannelService>().UpdateAsync(
            seeded.Channel.Id,
            new UpdateChannelRequest(CustomChatHeader: "h2 {{testharness}}\n"));
        host.Harness.ConfigureHeaderTag(new TestHarnessHeaderTagBehavior { Value = "tag-v2" });

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("second"));

        var second = host.Harness.ProviderRequests.Last();
        second.SystemPrompt.Should().Be("prompt-v2");
        second.Messages.Single(m => m.Role == "user").Content.Should().Be("h2 tag-v2\nsecond");
    }
}
