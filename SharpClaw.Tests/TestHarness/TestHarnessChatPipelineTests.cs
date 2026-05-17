using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessChatPipelineTests
{
    [Test]
    public async Task AllDynamicChatFeaturesDisabledSendsPlainSystemAndUserText()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true",
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            agentSystemPrompt: "plain system",
            disableToolSchemas: true);

        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("hello from user"));

        var request = host.Harness.ProviderRequests.Single();
        request.Surface.Should().Be("chat");
        request.SystemPrompt.Should().Be("plain system");
        request.Messages.Single(m => m.Role == "user").Content.Should().Be("hello from user");
        request.Tools.Should().BeEmpty();
    }

    [TestCase(true, false, "prefix {{testharness}}\nhello")]
    [TestCase(false, true, "prefix \nhello")]
    public async Task HeaderExpansionSwitchesControlModuleHeaderTags(
        bool disableExpansion,
        bool disableModuleTags,
        string expectedMessage)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableHeaderTagExpansion"] = disableExpansion.ToString(),
            ["Chat:DisableModuleHeaderTags"] = disableModuleTags.ToString()
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            customHeader: "prefix {{testharness}}\n");

        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("hello"));

        host.Harness.ProviderRequests.Single()
            .Messages.Single(m => m.Role == "user")
            .Content.Should().Be(expectedMessage);
        host.Harness.HeaderTagCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ModuleHeaderTagContextIsCapturedWhenExpansionIsEnabled()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigureHeaderTag(new TestHarnessHeaderTagBehavior { Value = "tag-value" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            customHeader: "prefix {{testharness}}\n");

        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("hello"));

        host.Harness.ProviderRequests.Single()
            .Messages.Single(m => m.Role == "user")
            .Content.Should().Be("prefix tag-value\nhello");

        var tagCall = host.Harness.HeaderTagCalls.Single();
        tagCall.Context.ChannelId.Should().Be(seeded.Channel.Id);
        tagCall.Context.AgentId.Should().Be(seeded.Agent.Id);
        tagCall.Context.ProviderKey.Should().Be(TestHarnessConstants.PlainProviderKey);
    }

    [Test]
    public async Task PermissionDeniedInlineToolDoesNotInvokeModuleTool()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureProviderToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedTool,
            """{}""",
            "after denial");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("call denied inline tool"));

        host.Harness.ToolCalls.Should().BeEmpty();
        response.AssistantMessage.Content.Should().Contain("after denial");
        host.Harness.ProviderRequests.Should().HaveCount(2);
        host.Harness.ProviderRequests.Last()
            .Messages.Single(m => m.Role == "tool")
            .Content.Should().Contain("permission denied");
    }

    [Test]
    public async Task PermissionGrantedInlineToolInvokesModuleTool()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "inline-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureProviderToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedTool,
            """{"result":"inline-ok"}""",
            "after inline");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("call allowed inline tool"));

        host.Harness.ToolCalls.Single().ToolName.Should().Be(TestHarnessConstants.InlinePermissionedTool);
        response.AssistantMessage.Content.Should().Contain("after inline");
    }

    [Test]
    public async Task PermissionDeniedJobToolCreatesDeniedJobWithoutInvokingModule()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureProviderToolThenFinal(
            host,
            TestHarnessConstants.JobPermissionedTool,
            """{}""",
            "after denied job");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("call denied job tool"));

        host.Harness.ToolCalls.Should().BeEmpty();
        response.JobResults.Should().NotBeNull();
        response.JobResults!.Single().Status.Should().Be(AgentJobStatus.Denied);
    }

    [Test]
    public async Task PermissionGrantedJobToolInvokesModuleAndPatchesJobCost()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "job-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.ToolProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls =
                        [
                            new ChatToolCall(
                                "call-job",
                                TestHarnessConstants.JobPermissionedTool,
                                """{"result":"job-ok"}""")
                        ],
                        Usage = new TokenUsage(11, 7)
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = "after job",
                        Usage = new TokenUsage(2, 3)
                    }
                ]
            });

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("call allowed job tool"));

        host.Harness.ToolCalls.Single().ToolName.Should().Be(TestHarnessConstants.JobPermissionedTool);
        var job = response.JobResults!.Single();
        job.Status.Should().Be(AgentJobStatus.Completed);
        job.JobCost.Should().NotBeNull();
        job.JobCost!.TotalTokens.Should().Be(18);
        response.AgentCost!.TotalTokens.Should().Be(23);
    }

    [Test]
    public async Task CachedCostHotPathDoesNotHitPersistenceAfterResponseCostsLoad()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("cache me"));

        host.PersistenceCounter.Reset();
        await host.Chat.GetChannelCostAsync(seeded.Channel.Id);
        await host.Chat.GetChannelCostAsync(seeded.Channel.Id);
        await host.Chat.GetAgentCostAsync(seeded.Agent.Id);
        await host.Chat.GetAgentCostAsync(seeded.Agent.Id);

        host.PersistenceCounter.QueryCalls.Should().Be(0);
    }

    [Test]
    public async Task NonStreamingChatMeetsHardProviderPlusOverheadBudget()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "budget-ok",
                        FirstTokenDelayMs = 50,
                        CompletionDelayMs = 50
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("budget"));
        sw.Stop();

        var timing = host.Harness.ProviderTimings.Single();
        timing.ConfiguredDelayMs.Should().Be(100);
        timing.ElapsedMs.Should().BeGreaterThanOrEqualTo(90);
        HarnessBudget.AssertWithin(sw.ElapsedMilliseconds, 100, 500, "non-streaming chat");
    }

    [Test]
    public async Task StreamingChatAndSseForwardingMeetHardProviderPlusOverheadBudget()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "stream-budget",
                        StreamingChunks = ["stream", "-", "budget"],
                        FirstTokenDelayMs = 40,
                        PerChunkDelayMs = 30,
                        CompletionDelayMs = 20
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in host.Chat.SendMessageStreamAsync(
            seeded.Channel.Id,
            new ChatRequest("stream budget"),
            (_, _) => Task.FromResult(true)))
        {
            events.Add(evt);
        }
        sw.Stop();

        events.Should().Contain(e => e.Type == ChatStreamEventType.Done);
        HarnessBudget.AssertWithin(sw.ElapsedMilliseconds, 120, 500, "streaming chat");

        host.Harness.Reset();
        seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "sse-budget",
                        StreamingChunks = ["sse", "-", "budget"],
                        FirstTokenDelayMs = 40,
                        PerChunkDelayMs = 30,
                        CompletionDelayMs = 20
                    }
                ]
            });

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        sw.Restart();
        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("sse budget"),
            host.Chat,
            NullLoggerFactory.Instance);
        sw.Stop();

        HarnessBudget.AssertWithin(sw.ElapsedMilliseconds, 120, 500, "SSE forwarding");
        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        sse.Should().Contain("event: TextDelta");
        sse.Should().Contain("event: Done");
    }

    [Test]
    public async Task ThreadedChatSendsNewestHistoryWithinConfiguredMessageLimit()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var thread = new ChatThreadDB
        {
            Id = Guid.NewGuid(),
            Name = "History Limit",
            ChannelId = seeded.Channel.Id,
            MaxMessages = 3,
            CreatedAt = now,
            UpdatedAt = now
        };
        host.Db.ChatThreads.Add(thread);
        for (var i = 0; i < 5; i++)
        {
            host.Db.ChatMessages.Add(new ChatMessageDB
            {
                Id = Guid.NewGuid(),
                Role = i % 2 == 0 ? ChatRoles.User : ChatRoles.Assistant,
                Origin = i % 2 == 0 ? MessageOrigin.User : MessageOrigin.Assistant,
                Content = $"history-{i}",
                ChannelId = seeded.Channel.Id,
                ThreadId = thread.Id,
                CreatedAt = now.AddSeconds(i),
                UpdatedAt = now.AddSeconds(i)
            });
        }

        await host.Db.SaveChangesAsync();

        await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("current"),
            thread.Id);

        host.Harness.ProviderRequests.Single()
            .Messages.Select(m => m.Content)
            .Should().Equal("history-2", "history-3", "history-4", "current");
    }

    [Test]
    public async Task RequestedAllowedAgentUsesThatAgentPromptAndSenderMetadata()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "primary system");
        var other = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Allowed Harness Agent",
            ModelId = seeded.Model.Id,
            Model = seeded.Model,
            RoleId = seeded.Role.Id,
            Role = seeded.Role,
            SystemPrompt = "allowed system",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.Agents.Add(other);
        seeded.Channel.AllowedAgents.Add(other);
        await host.Db.SaveChangesAsync();

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("use allowed", AgentId: other.Id));

        host.Harness.ProviderRequests.Single().SystemPrompt.Should().Be("allowed system");
        response.AssistantMessage.SenderAgentId.Should().Be(other.Id);
        response.AssistantMessage.SenderAgentName.Should().Be("Allowed Harness Agent");
        response.AgentCost!.AgentId.Should().Be(other.Id);
    }

    [Test]
    public async Task RequestedDisallowedAgentFailsBeforeProviderCall()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var other = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Disallowed Harness Agent",
            ModelId = seeded.Model.Id,
            Model = seeded.Model,
            RoleId = seeded.Role.Id,
            Role = seeded.Role,
            SystemPrompt = "blocked",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.Agents.Add(other);
        await host.Db.SaveChangesAsync();

        var act = () => host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("blocked", AgentId: other.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not allowed*");
        host.Harness.ProviderRequests.Should().BeEmpty();
    }

    [Test]
    public void BudgetHelperFailsAtOneMillisecondOverBudget()
    {
        var act = () => HarnessBudget.AssertWithin(1101, 1000, 100, "budget sample");
        act.Should().Throw<AssertionException>();
    }

    private static void ConfigureProviderToolThenFinal(
        ChatHarnessHost host,
        string toolName,
        string argumentsJson,
        string finalContent)
    {
        host.Harness.ConfigureProvider(
            TestHarnessConstants.ToolProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls = [new ChatToolCall("call-1", toolName, argumentsJson)],
                        Usage = new TokenUsage(3, 2)
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = finalContent,
                        Usage = new TokenUsage(4, 3)
                    }
                ]
            });
    }
}
