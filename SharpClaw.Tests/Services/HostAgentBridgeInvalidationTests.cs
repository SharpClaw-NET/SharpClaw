using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class HostAgentBridgeInvalidationTests
{
    [Test]
    public void Invalidate_WhenPermissionChanges_AppliesCorePlanToChatCache()
    {
        var cache = CreateCache();
        var bridge = CreateBridge(cache);
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
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

        bridge.Invalidate(TaskHostBridgeInvalidationTarget.Permission, userId);

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
    public void Invalidate_WhenTargetIsUnknown_ThrowsFromCorePlannerBeforeCacheMutation()
    {
        var cache = CreateCache();
        var bridge = CreateBridge(cache);
        var key = ChatCache.KeyHeaderUser(Guid.NewGuid());
        cache.Set(key, "user");

        var act = () => bridge.Invalidate(
            (TaskHostBridgeInvalidationTarget)999,
            Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("target");
        cache.TryGet<string>(key, out var value).Should().BeTrue();
        value.Should().Be("user");
    }

    private static HostAgentBridge CreateBridge(ChatCache cache) =>
        new(
            db: null!,
            taskService: null!,
            chatService: null!,
            scopeFactory: null!,
            cache,
            new TaskHostBridgeWorkflowEngine());

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
