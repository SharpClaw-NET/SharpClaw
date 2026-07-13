using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class TaskHostBridgeInvalidationPlannerTests
{
    [Test]
    public void BuildPlan_WhenAgentChanges_PreservesBroadAgentRuntimeInvalidations()
    {
        var plan = new TaskHostBridgeInvalidationPlanner().BuildPlan(
            TaskHostBridgeInvalidationTarget.Agent,
            Guid.NewGuid());

        typeof(TaskHostBridgeInvalidationPlanner).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        plan.Invalidations.Should().Equal(
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools));
    }

    [Test]
    public void BuildPlan_WhenChannelChanges_PreservesBroadChannelRuntimeInvalidations()
    {
        var plan = new TaskHostBridgeInvalidationPlanner().BuildPlan(
            TaskHostBridgeInvalidationTarget.Channel,
            Guid.NewGuid());

        plan.Invalidations.Should().Equal(
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools));
    }

    [Test]
    public void BuildPlan_WhenThreadChangesWithId_RemovesThreadHistoryAndHeaderSuffixes()
    {
        var threadId = Guid.NewGuid();

        var plan = new TaskHostBridgeInvalidationPlanner().BuildPlan(
            TaskHostBridgeInvalidationTarget.Thread,
            threadId);

        plan.Invalidations.Should().Equal(
            ChatCacheInvalidation.Key(ChatCache.KeyThreadHistoryLimits(threadId)),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix));
    }

    [Test]
    public void BuildPlan_WhenThreadChangesWithoutId_RemovesHeaderSuffixesOnly()
    {
        var plan = new TaskHostBridgeInvalidationPlanner().BuildPlan(
            TaskHostBridgeInvalidationTarget.Thread);

        plan.Invalidations.Should().Equal(
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix));
    }

    [Test]
    public void BuildPlan_WhenPermissionChanges_PreservesBroadPermissionInvalidations()
    {
        var plan = new TaskHostBridgeInvalidationPlanner().BuildPlan(
            TaskHostBridgeInvalidationTarget.Permission,
            Guid.NewGuid());

        plan.Invalidations.Should().Equal(
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderUser),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixHeaderAgentSuffix),
            ChatCacheInvalidation.Prefix(ChatCache.PrefixEffectiveTools));
    }

    [Test]
    public void BuildPlan_WhenTargetIsUnknown_PreservesOutOfRangeException()
    {
        var act = () => new TaskHostBridgeInvalidationPlanner().BuildPlan(
            (TaskHostBridgeInvalidationTarget)999,
            Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("target");
    }

    [Test]
    public void ApplyTo_WhenPermissionChanges_RemovesOnlyPreviouslyAppOwnedPrefixes()
    {
        var cache = CreateCache();
        var threadId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var userKey = ChatCache.KeyHeaderUser(userId);
        var suffixKey = ChatCache.KeyHeaderAgentSuffix(
            agentId,
            channelId,
            "provider",
            null);
        var toolsKey = ChatCache.KeyEffectiveTools(agentId, "all");
        var defaultResourceKey = ChatCache.KeyDefaultResourceResolution(
            channelId,
            agentId,
            "task");
        var threadKey = ChatCache.KeyThreadHistoryLimits(threadId);

        cache.Set(userKey, "user");
        cache.Set(suffixKey, "suffix");
        cache.Set(toolsKey, "tools");
        cache.Set(defaultResourceKey, "resource");
        cache.Set(threadKey, "thread");

        new TaskHostBridgeInvalidationPlanner()
            .BuildPlan(TaskHostBridgeInvalidationTarget.Permission)
            .ApplyTo(cache);

        cache.TryGet<string>(userKey, out _).Should().BeFalse();
        cache.TryGet<string>(suffixKey, out _).Should().BeFalse();
        cache.TryGet<string>(toolsKey, out _).Should().BeFalse();
        cache.TryGet<string>(defaultResourceKey, out var resource)
            .Should().BeTrue();
        cache.TryGet<string>(threadKey, out var thread).Should().BeTrue();
        resource.Should().Be("resource");
        thread.Should().Be("thread");
    }

    [Test]
    public void ApplyTo_WhenThreadChanges_RemovesThreadHistoryAndHeaderSuffixOnly()
    {
        var cache = CreateCache();
        var threadId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var userKey = ChatCache.KeyHeaderUser(Guid.NewGuid());
        var suffixKey = ChatCache.KeyHeaderAgentSuffix(
            agentId,
            channelId,
            "provider",
            null);
        var toolsKey = ChatCache.KeyEffectiveTools(agentId, "all");
        var threadKey = ChatCache.KeyThreadHistoryLimits(threadId);

        cache.Set(userKey, "user");
        cache.Set(suffixKey, "suffix");
        cache.Set(toolsKey, "tools");
        cache.Set(threadKey, "thread");

        new TaskHostBridgeInvalidationPlanner()
            .BuildPlan(TaskHostBridgeInvalidationTarget.Thread, threadId)
            .ApplyTo(cache);

        cache.TryGet<string>(threadKey, out _).Should().BeFalse();
        cache.TryGet<string>(suffixKey, out _).Should().BeFalse();
        cache.TryGet<string>(toolsKey, out var tools).Should().BeTrue();
        cache.TryGet<string>(userKey, out var user).Should().BeTrue();
        tools.Should().Be("tools");
        user.Should().Be("user");
    }

    private static ChatCache CreateCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:CacheMaxBytes"] = "1048576"
            })
            .Build();

        return new ChatCache(configuration);
    }
}
