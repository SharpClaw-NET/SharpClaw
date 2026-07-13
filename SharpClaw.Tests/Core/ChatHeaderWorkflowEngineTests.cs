using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatHeaderWorkflowEngineTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Test]
    public async Task BuildHeaderAsync_WhenCustomTemplateExists_ExpandsTemplateBeforeDefaultSuppression()
    {
        var engine = CreateEngine();
        var sessionUserId = Guid.NewGuid();
        var host = new HeaderHost { SessionUserId = sessionUserId };

        var header = await engine.BuildHeaderAsync(
            CreateRequest(
                channel: new ChannelDB
                {
                    Id = Guid.NewGuid(),
                    Title = "channel",
                    CustomChatHeader = "custom {{user-name}}"
                },
                disableDefaultHeaders: true),
            host);

        header.Should().Be("expanded:custom {{user-name}}:" + sessionUserId.ToString("N"));
        host.CustomExpansionCount.Should().Be(1);
        host.UserStateLoadCount.Should().Be(0);
        host.AgentSuffixLoadCount.Should().Be(0);
    }

    [Test]
    public async Task BuildHeaderAsync_WhenTaskContextExists_UsesTaskSharedDataAndAgentSuffix()
    {
        var engine = CreateEngine();
        var instanceId = Guid.NewGuid();
        var store = TaskSharedData.GetOrCreate(instanceId);
        store.TrySetLight("sync state").Should().BeTrue();
        store.TryWriteBig("memo", "Long plan", "body", out _).Should().BeTrue();

        try
        {
            var host = new HeaderHost
            {
                AgentSuffixFacts = new ChatAgentHeaderSuffixFacts(
                    "Worker",
                    ["ReadLogs"])
            };

            var header = await engine.BuildHeaderAsync(
                CreateRequest(
                    taskContext: new TaskChatContext(instanceId, "Nightly Sync")),
                host);

            header.Should().Contain("source: automated task");
            header.Should().Contain("task: Nightly Sync");
            header.Should().Contain("shared-data: sync state");
            header.Should().Contain("memo:\"Long plan\"");
            header.Should().Contain("agent-role: Worker (ReadLogs)");
            host.UserStateLoadCount.Should().Be(0);
            host.AgentSuffixLoadCount.Should().Be(1);
        }
        finally
        {
            TaskSharedData.Remove(instanceId);
        }
    }

    [Test]
    public async Task BuildHeaderAsync_WhenAuthenticatedUser_ReusesCachedUserStateAndAgentSuffix()
    {
        var engine = CreateEngine();
        var userId = Guid.NewGuid();
        var host = new HeaderHost
        {
            SessionUserId = userId,
            UserState = new ChatHeaderUserState(
                "marko",
                "Operator",
                ["ReadLogs"],
                "supervises agents"),
            AgentSuffixFacts = new ChatAgentHeaderSuffixFacts(
                "Worker",
                ["RunTasks"])
        };
        var request = CreateRequest();

        var first = await engine.BuildHeaderAsync(request, host);
        var second = await engine.BuildHeaderAsync(request, host);

        first.Should().Be(second);
        first.Should().Contain("user: marko");
        first.Should().Contain("role: Operator (ReadLogs)");
        first.Should().Contain("bio: supervises agents");
        first.Should().Contain("agent-role: Worker (RunTasks)");
        host.UserStateLoadCount.Should().Be(1);
        host.AgentSuffixLoadCount.Should().Be(1);
    }

    [Test]
    public async Task BuildHeaderAsync_WhenExternalUserHasNoSession_DoesNotLoadAuthenticatedUserState()
    {
        var engine = CreateEngine();
        var host = new HeaderHost
        {
            AgentSuffixFacts = new ChatAgentHeaderSuffixFacts(
                "Worker",
                [])
        };

        var header = await engine.BuildHeaderAsync(
            CreateRequest(
                externalUsername: "mkn8rn",
                externalDisplayName: "Marko",
                clientType: "discord"),
            host);

        header.Should().Contain("user: Marko (@mkn8rn)");
        header.Should().Contain("via: discord");
        host.UserStateLoadCount.Should().Be(0);
        host.AgentSuffixLoadCount.Should().Be(1);
    }

    [Test]
    public async Task BuildHeaderAsync_WhenNoSessionOrExternalUser_ReturnsNullWithoutLoadingSuffix()
    {
        var engine = CreateEngine();
        var host = new HeaderHost();

        var header = await engine.BuildHeaderAsync(CreateRequest(), host);

        header.Should().BeNull();
        host.UserStateLoadCount.Should().Be(0);
        host.AgentSuffixLoadCount.Should().Be(0);
    }

    [Test]
    public async Task BuildHeaderAsync_WhenReasoningEffortChanges_UsesSeparateAgentSuffixCacheKeys()
    {
        var engine = CreateEngine();
        var userId = Guid.NewGuid();
        var host = new HeaderHost
        {
            SessionUserId = userId,
            UserState = new ChatHeaderUserState("marko", null, [], null),
            AgentSuffixFacts = new ChatAgentHeaderSuffixFacts("Worker", [])
        };

        await engine.BuildHeaderAsync(
            CreateRequest(
                completionParameters: new CompletionParameters
                {
                    ReasoningEffort = "low"
                }),
            host);
        await engine.BuildHeaderAsync(
            CreateRequest(
                completionParameters: new CompletionParameters
                {
                    ReasoningEffort = "high"
                }),
            host);

        host.UserStateLoadCount.Should().Be(1);
        host.AgentSuffixLoadCount.Should().Be(2);
    }

    private static ChatHeaderWorkflowEngine CreateEngine()
        => new(
            new ChatDefaultHeaderEngine(
                new ProviderApiClientFactory([])),
            new ChatCache(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Chat:CacheMaxMegabytes"] = "1"
                        })
                    .Build()));

    private static ChatHeaderWorkflowRequest CreateRequest(
        ChannelDB? channel = null,
        AgentDB? agent = null,
        string clientType = "api",
        bool disableDefaultHeaders = false,
        TaskChatContext? taskContext = null,
        string? externalUsername = null,
        string? externalDisplayName = null,
        CompletionParameters? completionParameters = null,
        string providerKey = "test")
        => new(
            channel ?? new ChannelDB
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Title = "channel"
            },
            agent ?? new AgentDB
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Name = "Agent"
            },
            clientType,
            disableDefaultHeaders,
            taskContext,
            externalUsername,
            externalDisplayName,
            completionParameters,
            providerKey,
            Now);

    private sealed class HeaderHost : IChatHeaderWorkflowHost
    {
        public Guid? SessionUserId { get; init; }
        public ChatHeaderUserState? UserState { get; init; }
        public ChatAgentHeaderSuffixFacts AgentSuffixFacts { get; init; } =
            new(null, []);
        public int CustomExpansionCount { get; private set; }
        public int UserStateLoadCount { get; private set; }
        public int AgentSuffixLoadCount { get; private set; }

        public Guid? GetSessionUserId() => SessionUserId;

        public Task<string> ExpandCustomHeaderAsync(
            string template,
            ChannelDB channel,
            AgentDB agent,
            string clientType,
            Guid? sessionUserId,
            CompletionParameters? completionParameters,
            string providerKey,
            CancellationToken ct)
        {
            CustomExpansionCount++;
            return Task.FromResult(
                $"expanded:{template}:{sessionUserId?.ToString("N") ?? "none"}");
        }

        public Task<ChatHeaderUserState?> LoadUserHeaderStateAsync(
            Guid userId,
            CancellationToken ct)
        {
            UserStateLoadCount++;
            return Task.FromResult(UserState);
        }

        public Task<ChatAgentHeaderSuffixFacts> LoadAgentHeaderSuffixFactsAsync(
            Guid agentId,
            Guid channelId,
            CancellationToken ct)
        {
            AgentSuffixLoadCount++;
            return Task.FromResult(AgentSuffixFacts);
        }
    }
}
