using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessAgentOrchestrationExpandedTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(100)]
    [TestCase(1000)]
    public async Task ListAccessibleThreadsScalesAcrossThreadCounts(int threadCount)
    {
        await using var fixture = AoFixture.Create();
        var agent = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Primary Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        fixture.Db.Channels.AddRange(current, source);
        for (var i = 0; i < threadCount; i++)
            fixture.Db.ChatThreads.Add(MakeThread($"Thread {i:D4}", source.Id));
        await fixture.Db.SaveChangesAsync();

        var sw = Stopwatch.StartNew();
        var result = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            agent.Id,
            current.Id);
        sw.Stop();

        if (threadCount == 0)
        {
            result.Should().Contain("No accessible threads");
        }
        else
        {
            ParseArray(result).GetArrayLength().Should().Be(threadCount);
        }
        sw.ElapsedMilliseconds.Should().BeLessThan(2_000);
    }

    [Test]
    public async Task AdminIndependentCanListAndReadAccessibleThreads()
    {
        await using var fixture = AoFixture.Create();
        var agent = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Admin Independent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("Readable", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        fixture.Db.ChatMessages.Add(MakeMessage(source.Id, thread.Id, "readable text"));
        await fixture.Db.SaveChangesAsync();

        var list = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            agent.Id,
            current.Id);
        var read = await ExecuteAoInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{thread.Id:D}}"}""",
            agent.Id,
            current.Id);

        list.Should().Contain(thread.Id.ToString("D"));
        read.Should().Contain("readable text");
    }

    [Test]
    public async Task NonAdminDoubleGateRequiresRoleAndSourceChannelPermission()
    {
        await using var fixture = AoFixture.Create();
        var agent = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.ApprovedBySameLevelUser,
            "Double Gate Agent");
        var current = MakeChannel("Current", agent.Id);
        var hidden = MakeChannel("Hidden", agent.Id);
        var visible = MakeChannel("Visible", agent.Id);
        visible.PermissionSet = MakeCrossThreadPermissionSet(
            PermissionClearance.ApprovedBySameLevelUser);
        visible.PermissionSetId = visible.PermissionSet.Id;
        var hiddenThread = MakeThread("Hidden Thread", hidden.Id);
        var visibleThread = MakeThread("Visible Thread", visible.Id);

        fixture.Db.PermissionSets.Add(visible.PermissionSet);
        fixture.Db.Channels.AddRange(current, hidden, visible);
        fixture.Db.ChatThreads.AddRange(hiddenThread, visibleThread);
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            agent.Id,
            current.Id);

        result.Should().Contain(visibleThread.Id.ToString("D"));
        result.Should().NotContain(hiddenThread.Id.ToString("D"));
    }

    [Test]
    public async Task PrimaryAgentAndAllowedAgentsCanReadButUnassignedAgentCannot()
    {
        await using var fixture = AoFixture.Create();
        var primary = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Primary");
        var allowed = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Allowed");
        var unassigned = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Unassigned");
        var current = MakeChannel("Current", primary.Id);
        var primarySource = MakeChannel("Primary Source", primary.Id);
        var allowedSource = MakeChannel("Allowed Source", primary.Id);
        allowedSource.AllowedAgents.Add(allowed);
        var primaryThread = MakeThread("Primary Thread", primarySource.Id);
        var allowedThread = MakeThread("Allowed Thread", allowedSource.Id);
        fixture.Db.Channels.AddRange(current, primarySource, allowedSource);
        fixture.Db.ChatThreads.AddRange(primaryThread, allowedThread);
        await fixture.Db.SaveChangesAsync();

        var primaryList = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            primary.Id,
            current.Id);
        var allowedList = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            allowed.Id,
            current.Id);
        var unassignedList = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            unassigned.Id,
            current.Id);

        primaryList.Should().Contain(primaryThread.Id.ToString("D"));
        allowedList.Should().Contain(allowedThread.Id.ToString("D"));
        unassignedList.Should().Contain("No accessible threads");
    }

    [Test]
    public async Task DisabledAccessibleThreadsHeaderReturnsEmptyButExplicitToolsStillWork()
    {
        await using var fixture = AoFixture.Create(new Dictionary<string, string?>
        {
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true"
        });
        var agent = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "Header Disabled Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("Visible Thread", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        await fixture.Db.SaveChangesAsync();

        var tag = fixture.Registry.GetHeaderTag("accessible-threads")!;
        var header = await tag.ResolveWithContext!(
            fixture.Services,
            new ModuleHeaderTagContext(
                current.Id,
                current.Title,
                agent.Id,
                agent.Name,
                "api",
                UserId: null,
                CompletionParameters: null,
                ProviderKey: "test"),
            default);
        var explicitList = await ExecuteAoInlineToolAsync(
            fixture,
            "list_accessible_threads",
            "{}",
            agent.Id,
            current.Id);

        header.Should().Be("");
        explicitList.Should().Contain(thread.Id.ToString("D"));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(50)]
    [TestCase(500)]
    public async Task ReadThreadHistoryHandlesEmptyShortLongAndVeryLargeHistories(int messageCount)
    {
        await using var fixture = AoFixture.Create();
        var agent = SeedAgentWithCrossThreadRole(
            fixture.Db,
            PermissionClearance.Independent,
            "History Agent");
        var current = MakeChannel("Current", agent.Id);
        var source = MakeChannel("Source", agent.Id);
        var thread = MakeThread("History", source.Id);
        fixture.Db.Channels.AddRange(current, source);
        fixture.Db.ChatThreads.Add(thread);
        for (var i = 0; i < messageCount; i++)
            fixture.Db.ChatMessages.Add(MakeMessage(source.Id, thread.Id, $"message-{i:D4}"));
        await fixture.Db.SaveChangesAsync();

        var result = await ExecuteAoInlineToolAsync(
            fixture,
            "read_thread_history",
            $$"""{"threadId":"{{thread.Id:D}}","maxMessages":200}""",
            agent.Id,
            current.Id);

        if (messageCount == 0)
        {
            result.Should().Contain("Thread exists but has no messages");
        }
        else
        {
            ParseArray(result).GetArrayLength().Should().Be(Math.Min(messageCount, 200));
        }
    }

    private static async Task<string> ExecuteAoInlineToolAsync(
        AoFixture fixture,
        string toolName,
        string json,
        Guid agentId,
        Guid channelId)
    {
        using var doc = JsonDocument.Parse(json);
        return await fixture.Module.ExecuteInlineToolAsync(
            toolName,
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "call"),
            fixture.Services,
            default);
    }

    private static JsonElement ParseArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AgentDB SeedAgentWithCrossThreadRole(
        SharpClawDbContext db,
        PermissionClearance clearance,
        string name)
    {
        var permissionSet = MakeCrossThreadPermissionSet(clearance);
        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = name + " Role",
            PermissionSetId = permissionSet.Id,
            PermissionSet = permissionSet,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = name,
            ModelId = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.PermissionSets.Add(permissionSet);
        db.Roles.Add(role);
        db.Agents.Add(agent);
        return agent;
    }

    private static PermissionSetDB MakeCrossThreadPermissionSet(PermissionClearance clearance) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        GlobalFlags =
        [
            new GlobalFlagDB
            {
                Id = Guid.NewGuid(),
                FlagKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory,
                Clearance = clearance,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        ]
    };

    private static ChannelDB MakeChannel(string title, Guid? agentId) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        AgentId = agentId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ChatThreadDB MakeThread(string name, Guid channelId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ChannelId = channelId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ChatMessageDB MakeMessage(Guid channelId, Guid threadId, string content) => new()
    {
        Id = Guid.NewGuid(),
        Role = "user",
        Content = content,
        ChannelId = channelId,
        ThreadId = threadId,
        SenderUsername = "tester",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class AoFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private AoFixture(ServiceProvider provider, AsyncServiceScope scope, AgentOrchestrationModule module)
        {
            _provider = provider;
            _scope = scope;
            Module = module;
        }

        public IServiceProvider Services => _scope.ServiceProvider;
        public SharpClawDbContext Db => Services.GetRequiredService<SharpClawDbContext>();
        public ModuleRegistry Registry => _provider.GetRequiredService<ModuleRegistry>();
        public AgentOrchestrationModule Module { get; }

        public static AoFixture Create(IReadOnlyDictionary<string, string?>? settings = null)
        {
            var module = new AgentOrchestrationModule();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddDbContext<SharpClawDbContext>(
                options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services.AddScoped<ISharpClawDataContext>(
                sp => sp.GetRequiredService<SharpClawDbContext>());
            services.AddSingleton<ModuleRegistry>();
            module.ConfigureServices(services);

            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ModuleRegistry>().Register(module);
            return new AoFixture(provider, provider.CreateAsyncScope(), module);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
