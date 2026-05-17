using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class ChatCacheTests
{
    [Test]
    public async Task GetOrCreateAsync_CachesValueUntilBudgetEvicts()
    {
        var cache = CreateCache(cacheBytes: 1_000_000);
        var calls = 0;

        var first = await cache.GetOrCreateAsync(
            "key",
            _ =>
            {
                calls++;
                return Task.FromResult<string?>("value");
            },
            ct: CancellationToken.None);
        var second = await cache.GetOrCreateAsync(
            "key",
            _ =>
            {
                calls++;
                return Task.FromResult<string?>("other");
            },
            ct: CancellationToken.None);

        first.Should().Be("value");
        second.Should().Be("value");
        calls.Should().Be(1);
    }

    [Test]
    public async Task CacheMaxBytesZero_DisablesChatCache()
    {
        var cache = CreateCache(cacheBytes: 0);
        var calls = 0;

        await cache.GetOrCreateAsync(
            "key",
            _ =>
            {
                calls++;
                return Task.FromResult<string?>("value");
            },
            ct: CancellationToken.None);
        await cache.GetOrCreateAsync(
            "key",
            _ =>
            {
                calls++;
                return Task.FromResult<string?>("value");
            },
            ct: CancellationToken.None);

        calls.Should().Be(2);
    }

    [Test]
    public async Task ChatCache_EvictsFirstInsertedEntryWhenBudgetIsExceeded()
    {
        var cache = CreateCache(cacheBytes: 100);

        await cache.GetOrCreateAsync(
            "first",
            _ => Task.FromResult<string?>("first"),
            _ => 60,
            CancellationToken.None);

        await cache.GetOrCreateAsync(
            "second",
            _ => Task.FromResult<string?>("second"),
            _ => 60,
            CancellationToken.None);

        cache.TryGet<string>("first", out _).Should().BeFalse();
        cache.TryGet<string>("second", out var second).Should().BeTrue();
        second.Should().Be("second");
    }

    [Test]
    public async Task RemoveByPrefix_RemovesMatchingEntriesOnly()
    {
        var cache = CreateCache(cacheBytes: 1_000_000);

        await cache.GetOrCreateAsync(
            "prefix:one",
            _ => Task.FromResult<string?>("one"),
            _ => 8,
            CancellationToken.None);
        await cache.GetOrCreateAsync(
            "other:two",
            _ => Task.FromResult<string?>("two"),
            _ => 8,
            CancellationToken.None);

        cache.RemoveByPrefix("prefix:");

        cache.TryGet<string>("prefix:one", out _).Should().BeFalse();
        cache.TryGet<string>("other:two", out var remaining).Should().BeTrue();
        remaining.Should().Be("two");
    }

    [Test]
    public async Task RecordAssistantTokens_UpdatesActiveCostSnapshots()
    {
        var cache = CreateCache(cacheBytes: 1_000_000);
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await cache.GetChannelCostAsync(
            channelId,
            _ => Task.FromResult(new ChannelCostResponse(
                channelId,
                10,
                5,
                15,
                [new AgentTokenBreakdown(agentId, "agent", 10, 5, 15)])),
            CancellationToken.None);

        await cache.GetThreadCostAsync(
            channelId,
            threadId,
            _ => Task.FromResult<ThreadCostResponse?>(new ThreadCostResponse(
                threadId,
                channelId,
                7,
                3,
                10,
                [new AgentTokenBreakdown(agentId, "agent", 7, 3, 10)])),
            CancellationToken.None);

        await cache.GetAgentCostAsync(
            agentId,
            _ => Task.FromResult<AgentCostResponse?>(new AgentCostResponse(
                agentId,
                "agent",
                10,
                5,
                15,
                [new AgentChannelTokenBreakdown(channelId, 10, 5, 15)])),
            CancellationToken.None);

        cache.RecordAssistantTokens(channelId, threadId, agentId, "agent", 4, 2);

        var channel = await cache.GetChannelCostAsync(
            channelId,
            _ => throw new InvalidOperationException("Channel cache should be hot."),
            CancellationToken.None);
        var thread = await cache.GetThreadCostAsync(
            channelId,
            threadId,
            _ => throw new InvalidOperationException("Thread cache should be hot."),
            CancellationToken.None);
        var agent = await cache.GetAgentCostAsync(
            agentId,
            _ => throw new InvalidOperationException("Agent cache should be hot."),
            CancellationToken.None);

        channel.TotalTokens.Should().Be(21);
        channel.AgentBreakdown.Single().TotalTokens.Should().Be(21);
        thread!.TotalTokens.Should().Be(16);
        thread.AgentBreakdown.Single().TotalTokens.Should().Be(16);
        agent!.TotalTokens.Should().Be(21);
        agent.ChannelBreakdown.Single().TotalTokens.Should().Be(21);
    }

    private static ChatCache CreateCache(long cacheBytes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:CacheMaxBytes"] = cacheBytes.ToString()
            })
            .Build();

        return new ChatCache(configuration);
    }

}
