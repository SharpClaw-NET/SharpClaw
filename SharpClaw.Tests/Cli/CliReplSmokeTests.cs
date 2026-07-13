using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host.Cli;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.Cli;

[TestFixture]
[NonParallelizable]
public sealed class CliReplSmokeTests
{
    private const string Password = "123456";

    [Test]
    public async Task ReplLoginChannelThreadChatAndCostWorkflowUsesRealParser()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "plain cli system",
            disableToolSchemas: true,
            includeUser: false);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "cli smoke response",
                        StreamingChunks = ["cli ", "smoke ", "response"],
                        Usage = new TokenUsage(3, 4)
                    }
                ]
            });

        var user = UniqueUser();
        var channelTitle = "CLI Smoke Channel " + Guid.NewGuid().ToString("N");
        var threadName = "CLI Smoke Thread " + Guid.NewGuid().ToString("N");

        var run = await RunReplAsync(host.Services, $"""
            register {user} {Password}
            login {user} {Password}
            me
            channel add --agent {seeded.Agent.Id} --no-tools "{channelTitle}"
            thread add "{threadName}" --max-messages 5 --max-chars 80
            exit
            """);

        run.Error.Should().BeEmpty();
        run.Output.Should().Contain(user);
        run.Output.Should().Contain(channelTitle);
        run.Output.Should().Contain(threadName);

        var (channel, thread) = await QueryAsync(host, async db =>
        {
            var channel = await db.Channels.SingleAsync(c =>
                c.AgentId == seeded.Agent.Id
                && c.DisableToolSchemas
                && c.Id != seeded.Channel.Id);
            var thread = await db.ChatThreads.SingleAsync(t => t.Name == threadName);
            return (channel, thread);
        });

        channel.Title.Should().Be(channelTitle);
        thread.ChannelId.Should().Be(channel.Id);
        thread.Name.Should().Be(threadName);
        thread.MaxMessages.Should().Be(5);
        thread.MaxCharacters.Should().Be(80);

        var chat = await RunReplAsync(host.Services, $"""
            login {user} {Password}
            thread select {thread.Id}
            chat --thread {thread.Id} hello from cli smoke
            exit
            """);

        chat.Error.Should().BeEmpty();
        chat.Output.Should().Contain("cli smoke response");

        var cost = await RunCommandAsync(host.Services, "thread", "cost", thread.Id.ToString());
        cost.Error.Should().BeEmpty();
        cost.Output.Should().Contain("\"TotalPromptTokens\": 3");
        cost.Output.Should().Contain("\"TotalCompletionTokens\": 4");
        cost.Output.Should().Contain("\"TotalTokens\": 7");

        host.Harness.ProviderRequests.Should().ContainSingle()
            .Which.Messages.Last().Content.Should().Be("hello from cli smoke");
        channel.DisableToolSchemas.Should().BeTrue();
    }

    [Test]
    public async Task ReplDefaultsSetAndClearRemoveCoreKeysForChannelsAndContexts()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            includeUser: false);
        var contextId = await QueryAsync(host, async db =>
        {
            var context = new ChannelContextDB
            {
                Id = Guid.NewGuid(),
                Name = "CLI defaults context",
                AgentId = seeded.Agent.Id
            };
            db.AgentContexts.Add(context);
            await db.SaveChangesAsync();
            return context.Id;
        });

        var user = UniqueUser();
        var run = await RunReplAsync(host.Services, $"""
            register {user} {Password}
            login {user} {Password}
            channel defaults {seeded.Channel.Id} set agent {seeded.Agent.Id}
            channel defaults {seeded.Channel.Id} clear agent
            channel defaults {seeded.Channel.Id}
            context defaults {contextId} set agent {seeded.Agent.Id}
            context defaults {contextId} clear agent
            context defaults {contextId}
            exit
            """);

        run.Error.Should().BeEmpty();
        run.Output.Should().Contain("\"Entries\": {}");

        var entries = await QueryAsync(host, async db =>
        {
            var channel = await db.Channels
                .Include(c => c.DefaultResourceSet!).ThenInclude(s => s.Entries)
                .SingleAsync(c => c.Id == seeded.Channel.Id);
            var context = await db.AgentContexts
                .Include(c => c.DefaultResourceSet!).ThenInclude(s => s.Entries)
                .SingleAsync(c => c.Id == contextId);
            return (
                ChannelEntries: channel.DefaultResourceSet?.Entries.Count ?? 0,
                ContextEntries: context.DefaultResourceSet?.Entries.Count ?? 0);
        });

        entries.ChannelEntries.Should().Be(0);
        entries.ContextEntries.Should().Be(0);
    }

    [Test]
    public async Task ReplJobSubmitListStatusAndLifecycleUseRealJsonArgumentParsing()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "job-default" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true,
            includeUser: false);
        var user = UniqueUser();
        var completedParams = """{"result":"cli-job"}""";
        var runningParams = """{"result":"long-running","remainExecuting":true}""";

        var submit = await RunReplAsync(host.Services, $"""
            register {user} {Password}
            login {user} {Password}
            job submit {seeded.Channel.Id} {TestHarnessConstants.JobPermissionedTool} --params {completedParams}
            job submit {seeded.Channel.Id} {TestHarnessConstants.JobPermissionedTool} --params {runningParams}
            job list {seeded.Channel.Id}
            exit
            """);

        submit.Error.Should().BeEmpty();
        submit.Output.Should().Contain("\"ResultData\": \"cli-job\"");
        submit.Output.Should().Contain("\"Status\": \"Executing\"");

        var jobs = await QueryAsync(host, async db => await db.AgentJobs
            .Where(j => j.ChannelId == seeded.Channel.Id)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync());
        jobs.Should().HaveCount(2);
        var completed = jobs[0];
        var running = jobs[1];
        completed.Status.Should().Be(AgentJobStatus.Completed);
        completed.ScriptJson.Should().Be(completedParams);
        running.Status.Should().Be(AgentJobStatus.Executing);
        running.ScriptJson.Should().Be(runningParams);

        var lifecycle = await RunReplAsync(host.Services, $"""
            login {user} {Password}
            job status {running.Id}
            job pause {running.Id}
            job resume {running.Id}
            job cancel {running.Id}
            job status {running.Id}
            exit
            """);

        lifecycle.Error.Should().BeEmpty();
        lifecycle.Output.Should().Contain("\"Status\": \"Paused\"");
        lifecycle.Output.Should().Contain("\"Status\": \"Cancelled\"");

        var finalStatus = await QueryAsync(host, async db =>
            await db.AgentJobs
                .Where(j => j.Id == running.Id)
                .Select(j => j.Status)
                .SingleAsync());
        finalStatus.Should().Be(AgentJobStatus.Cancelled);
    }

    private static string UniqueUser() => "cli_" + Guid.NewGuid().ToString("N");

    private static async Task<T> QueryAsync<T>(
        ChatHarnessHost host,
        Func<SharpClawDbContext, Task<T>> query)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        return await query(db);
    }

    private static async Task<(string Output, string Error)> RunCommandAsync(
        IServiceProvider services,
        params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var handled = await CliDispatcher.TryHandleAsync(args, services);
            handled.Should().BeTrue();
            return (output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<(string Output, string Error)> RunReplAsync(
        IServiceProvider services,
        string script)
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalForceRepl = Environment.GetEnvironmentVariable("SHARPCLAW_FORCE_REPL");

        using var input = new StringReader(script.ReplaceLineEndings(Environment.NewLine));
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        Console.SetIn(input);
        Console.SetOut(output);
        Console.SetError(error);
        Environment.SetEnvironmentVariable("SHARPCLAW_FORCE_REPL", "1");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ran = await CliDispatcher.RunInteractiveAsync(services, cts.Token);
            ran.Should().BeTrue();
            return (output.ToString(), error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPCLAW_FORCE_REPL", originalForceRepl);
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
