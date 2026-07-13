using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessToolCorrectnessExpandedTests
{
    [Test]
    public async Task AllowedInlineModuleToolSucceedsThroughAliasOnce()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "alias-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedToolAlias,
            """{"result":"alias-ok"}""",
            "alias final");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("alias"));

        response.AssistantMessage.Content.Should().Contain("alias final");
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be(TestHarnessConstants.InlinePermissionedTool);
    }

    [Test]
    public async Task DeniedInlineModuleToolAliasIsBlockedByHostPermissionEnforcement()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedToolAlias,
            """{}""",
            "denied final");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("denied alias"));

        host.Harness.ToolCalls.Should().BeEmpty();
        host.Harness.ProviderRequests.Last()
            .Messages.Single(m => m.Role == "tool")
            .Content.Should().Contain("permission denied");
    }

    [Test]
    public async Task ToolPermissionDoesNotStayCachedAfterRolePrivilegeChange()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "first-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedTool,
            """{}""",
            "first final");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("first"));
        host.Harness.ToolCalls.Should().ContainSingle();

        var flags = await host.Db.GlobalFlags
            .Where(f => f.PermissionSetId == seeded.PermissionSet.Id)
            .ToListAsync();
        host.Db.GlobalFlags.RemoveRange(flags);
        await host.Db.SaveChangesAsync();
        host.Harness.Reset();
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedTool,
            """{}""",
            "second final");

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("second"));

        host.Harness.ToolCalls.Should().BeEmpty();
        host.Harness.ProviderRequests.Last()
            .Messages.Single(m => m.Role == "tool")
            .Content.Should().Contain("permission denied");
    }

    [Test]
    public async Task MalformedToolArgumentsProduceStableError()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlineOpenTool,
            "{",
            "after malformed");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("malformed"));

        host.Harness.ToolCalls.Should().BeEmpty();
        response.AssistantMessage.Content.Should().Contain("after malformed");
        host.Harness.ProviderRequests.Last()
            .Messages.Single(m => m.Role == "tool")
            .Content.Should().Be("Error: malformed tool arguments JSON.");
    }

    [Test]
    public async Task LargeToolOutputIsBoundedInProviderCaptureAndStillHandled()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigureOpenInlineTool(new TestHarnessToolBehavior
        {
            Result = "large-tool",
            PayloadBytes = 128_000
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlineOpenTool,
            """{}""",
            "after large");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("large"));

        response.AssistantMessage.Content.Should().Contain("after large");
        var toolMessage = host.Harness.ProviderRequests.Last()
            .Messages.Single(m => m.Role == "tool")
            .Content!;
        toolMessage.Length.Should().BeLessThan(65_000);
        toolMessage.Should().Contain("[truncated]");
    }

    [Test]
    public async Task ToolLatencyIsMeasuredSeparatelyFromProviderLatency()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior
        {
            LatencyMs = 120,
            Result = "slow-tool"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.InlinePermissionedTool,
            """{}""",
            "after slow tool");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("slow tool"));

        response.AssistantMessage.Content.Should().Contain("after slow tool");
        host.Harness.ToolCalls.Single().ElapsedMs.Should().BeGreaterThanOrEqualTo(100);
        host.Harness.ProviderTimings.Sum(t => t.ConfiguredDelayMs).Should().Be(0);
    }

    [Test]
    public async Task JobToolAliasUsesCanonicalPermissionDescriptorAndDoesNotBypassHost()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        ConfigureToolThenFinal(
            host,
            TestHarnessConstants.JobPermissionedToolAlias,
            """{}""",
            "after denied job alias");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("job alias"));

        response.JobResults!.Single().Status.Should().Be(SharpClaw.Contracts.Enums.AgentJobStatus.Denied);
        host.Harness.ToolCalls.Should().BeEmpty();
    }

    private static void ConfigureToolThenFinal(
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
