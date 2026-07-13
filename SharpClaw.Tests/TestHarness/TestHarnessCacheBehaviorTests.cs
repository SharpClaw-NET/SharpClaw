using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Enums;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessCacheBehaviorTests
{
    [Test]
    public async Task ChannelMutationInvalidatesOnlyThatChannelHeaderSuffix()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var sibling = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Sibling Channel",
            AgentId = seeded.Agent.Id,
            Agent = seeded.Agent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.Channels.Add(sibling);
        await host.Db.SaveChangesAsync();

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm channel a"));
        await host.Chat.SendMessageAsync(sibling.Id, new ChatRequest("warm channel b"));

        var cache = host.Services.GetRequiredService<ChatCache>();
        var primaryKey = HeaderSuffixKey(seeded, seeded.Channel.Id);
        var siblingKey = HeaderSuffixKey(seeded, sibling.Id);
        cache.TryGet<string>(primaryKey, out _).Should().BeTrue();
        cache.TryGet<string>(siblingKey, out _).Should().BeTrue();

        await host.Services.GetRequiredService<ChannelService>().UpdateAsync(
            seeded.Channel.Id,
            new UpdateChannelRequest(CustomChatHeader: "mutated {{agent-name}}"));

        cache.TryGet<string>(primaryKey, out _).Should().BeFalse();
        cache.TryGet<string>(siblingKey, out _).Should().BeTrue();
    }

    [Test]
    public async Task AgentMutationInvalidatesOnlyThatAgentsHeaderAndToolCaches()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var first = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var second = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);

        await host.Chat.SendMessageAsync(first.Channel.Id, new ChatRequest("warm first"));
        await host.Chat.SendMessageAsync(second.Channel.Id, new ChatRequest("warm second"));

        var cache = host.Services.GetRequiredService<ChatCache>();
        var firstHeader = HeaderSuffixKey(first, first.Channel.Id);
        var secondHeader = HeaderSuffixKey(second, second.Channel.Id);
        var firstTools = ChatCache.KeyEffectiveTools(first.Agent.Id, "all");
        var secondTools = ChatCache.KeyEffectiveTools(second.Agent.Id, "all");
        cache.TryGet<string>(firstHeader, out _).Should().BeTrue();
        cache.TryGet<string>(secondHeader, out _).Should().BeTrue();
        cache.TryGet<object>(firstTools, out _).Should().BeTrue();
        cache.TryGet<object>(secondTools, out _).Should().BeTrue();

        await host.Services.GetRequiredService<AgentService>().UpdateAsync(
            first.Agent.Id,
            new UpdateAgentRequest(CustomChatHeader: "agent-one {{agent-name}}"));

        cache.TryGet<string>(firstHeader, out _).Should().BeFalse();
        cache.TryGet<object>(firstTools, out _).Should().BeFalse();
        cache.TryGet<string>(secondHeader, out _).Should().BeTrue();
        cache.TryGet<object>(secondTools, out _).Should().BeTrue();
    }

    [Test]
    public async Task ThreadLimitUpdateInvalidatesWarmHistoryLimit()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var threads = host.Services.GetRequiredService<ThreadService>();
        var thread = await threads.CreateAsync(
            seeded.Channel.Id,
            new CreateThreadRequest("Cache Thread", MaxMessages: 1));

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("first"), thread.Id);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("second"), thread.Id);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("third"), thread.Id);
        host.Harness.ProviderRequests.Last().Messages.Should().HaveCount(2);

        await threads.UpdateAsync(thread.Id, new UpdateThreadRequest(MaxMessages: 10));
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("fourth"), thread.Id);

        host.Harness.ProviderRequests.Last().Messages.Count.Should().BeGreaterThan(2);
    }

    [Test]
    public async Task DefaultResourceMutationInvalidatesCachedJobResolution()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "ok" });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var firstResource = Guid.NewGuid();
        var secondResource = Guid.NewGuid();
        GrantHarnessResource(host, seeded.PermissionSet, firstResource);
        GrantHarnessResource(host, seeded.PermissionSet, secondResource);
        await host.Db.SaveChangesAsync();

        var defaults = host.Services.GetRequiredService<DefaultResourceSetService>();
        await defaults.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            firstResource);

        var jobs = host.Services.GetRequiredService<AgentJobService>();
        var firstJob = await SubmitResourceJobAsync(jobs, seeded.Channel.Id);
        firstJob.Status.Should().Be(AgentJobStatus.Completed);
        firstJob.ResourceId.Should().Be(firstResource);

        var cache = host.Services.GetRequiredService<ChatCache>();
        var resolutionKey = ChatCache.KeyDefaultResourceResolution(
            seeded.Channel.Id,
            seeded.Agent.Id,
            TestHarnessConstants.JobResourceTool);
        cache.TryGet<object>(resolutionKey, out _).Should().BeTrue();

        await defaults.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            secondResource);
        cache.TryGet<object>(resolutionKey, out _).Should().BeFalse();

        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "ok" });
        var secondJob = await SubmitResourceJobAsync(jobs, seeded.Channel.Id);
        secondJob.Status.Should().Be(AgentJobStatus.Completed);
        secondJob.ResourceId.Should().Be(secondResource);
    }

    [Test]
    public async Task EffectiveToolDefinitionsStayWarmUntilAgentToolSettingsChange()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm tools"));
        host.Harness.ProviderRequests.Last().Tools.Should().NotBeEmpty();
        var cache = host.Services.GetRequiredService<ChatCache>();
        var toolsKey = ChatCache.KeyEffectiveTools(seeded.Agent.Id, "all");
        cache.TryGet<object>(toolsKey, out _).Should().BeTrue();

        host.Harness.ResetDiagnostics();
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("still warm"));
        host.Harness.PermissionDescriptorBuilds.Should().Be(0);
        cache.TryGet<object>(toolsKey, out _).Should().BeTrue();

        await host.Services.GetRequiredService<AgentService>().UpdateAsync(
            seeded.Agent.Id,
            new UpdateAgentRequest(DisableToolSchemas: true));
        cache.TryGet<object>(toolsKey, out _).Should().BeFalse();
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ColdChatAfterCacheClear_NoDynamicHeaders_Under250ms()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm"));
        host.Services.GetRequiredService<ChatCache>().Clear();
        host.Harness.Reset();

        var sw = Stopwatch.StartNew();
        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("cold after clear"));
        sw.Stop();

        response.AssistantMessage.Content.Should().Be("test harness response");
        sw.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(250);
    }

    private static string HeaderSuffixKey(SeededChat seeded, Guid channelId) =>
        ChatCache.KeyHeaderAgentSuffix(
            seeded.Agent.Id,
            channelId,
            seeded.Provider.ProviderKey,
            seeded.Agent.ReasoningEffort);

    private static async Task<AgentJobResponse> SubmitResourceJobAsync(
        AgentJobService jobs,
        Guid channelId) =>
        await jobs.SubmitAsync(
            channelId,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobResourceTool,
                ScriptJson: """{"result":"ok"}"""));

    private static void GrantHarnessResource(
        ChatHarnessHost host,
        PermissionSetDB permissionSet,
        Guid resourceId)
    {
        host.Db.ResourceAccesses.Add(new ResourceAccessDB
        {
            Id = Guid.NewGuid(),
            PermissionSetId = permissionSet.Id,
            ResourceType = TestHarnessConstants.ResourceType,
            ResourceId = resourceId,
            Clearance = PermissionClearance.Independent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }
}
