using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Tests.TestHarness;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessDefaultsAndResourcesTests
{
    [Test]
    public async Task ChannelDefaultsClearRemovesKeyPermanently()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var service = host.Services.GetRequiredService<DefaultResourceSetService>();
        var resourceId = Guid.NewGuid();

        await service.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            resourceId);
        await service.ClearKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey);

        var result = await service.GetForChannelAsync(seeded.Channel.Id);
        result!.Entries.Should().NotContainKey(TestHarnessConstants.DefaultResourceKey);
    }

    [Test]
    public async Task ContextDefaultsClearRemovesKeyPermanently()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var context = new SharpClaw.Contracts.Entities.Core.Context.ChannelContextDB
        {
            Id = Guid.NewGuid(),
            Name = "Defaults Context",
            AgentId = seeded.Agent.Id,
            Agent = seeded.Agent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.AgentContexts.Add(context);
        await host.Db.SaveChangesAsync();
        var service = host.Services.GetRequiredService<DefaultResourceSetService>();

        await service.SetKeyForContextAsync(
            context.Id,
            TestHarnessConstants.DefaultResourceKey,
            Guid.NewGuid());
        await service.ClearKeyForContextAsync(
            context.Id,
            TestHarnessConstants.DefaultResourceKey);

        var result = await service.GetForContextAsync(context.Id);
        result!.Entries.Should().NotContainKey(TestHarnessConstants.DefaultResourceKey);
    }

    [Test]
    public async Task ClearingCoreAgentDefaultWorksWhenAgentOrchestrationIsDisabled()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var service = host.Services.GetRequiredService<DefaultResourceSetService>();

        service.IsValidKey("agent").Should().BeTrue();
        await service.SetKeyForChannelAsync(seeded.Channel.Id, "agent", seeded.Agent.Id);
        await service.ClearKeyForChannelAsync(seeded.Channel.Id, "agent");

        var result = await service.GetForChannelAsync(seeded.Channel.Id);
        result!.Entries.Should().NotContainKey("agent");
    }

    [Test]
    public async Task SetClearSetSameDefaultKeySurvivesContextReload()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var service = host.Services.GetRequiredService<DefaultResourceSetService>();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await service.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            first);
        await service.ClearKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey);
        host.Db.ChangeTracker.Clear();
        await service.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            second);

        var result = await service.GetForChannelAsync(seeded.Channel.Id);
        result!.Entries[TestHarnessConstants.DefaultResourceKey].Should().Be(second);
    }

    [Test]
    public async Task UnknownDefaultKeyClearIsIdempotent()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var service = host.Services.GetRequiredService<DefaultResourceSetService>();

        await service.ClearKeyForChannelAsync(seeded.Channel.Id, "unknown_key");
        await service.ClearKeyForChannelAsync(seeded.Channel.Id, "unknown_key");

        var result = await service.GetForChannelAsync(seeded.Channel.Id);
        result!.Entries.Should().BeEmpty();
    }

    [Test]
    public void ModuleDefinedDefaultResourceKeyCannotShadowCoreAgentKey()
    {
        var registry = new ModuleRegistry();

        registry.Register(new ShadowCoreAgentDefaultModule());

        registry.IsRegisteredDefaultResourceKey("agent").Should().BeTrue();
        registry.GetDescriptorByDefaultResourceKey("agent").Should().BeNull();
        registry.GetAllDefaultResourceKeys().Count(k => string.Equals(k, "agent", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Test]
    public async Task PerResourceJobUsesChannelDefaultResourceWhenRequestOmitsResource()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "resource-ok" });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var resourceId = Guid.NewGuid();
        GrantHarnessResource(host, seeded.PermissionSet, resourceId);
        await host.Db.SaveChangesAsync();

        var defaults = host.Services.GetRequiredService<DefaultResourceSetService>();
        await defaults.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            resourceId);

        var job = await host.Services.GetRequiredService<AgentJobService>().SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobResourceTool,
                ScriptJson: """{"result":"resource-ok"}"""));

        job.Status.Should().Be(AgentJobStatus.Completed);
        job.ResourceId.Should().Be(resourceId);
        job.ResultData.Should().Be("resource-ok");
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be(TestHarnessConstants.JobResourceTool);
    }

    [Test]
    public async Task PerResourceJobPrefersChannelDefaultOverContextDefault()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "channel-default" });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var contextResourceId = Guid.NewGuid();
        var channelResourceId = Guid.NewGuid();
        GrantHarnessResource(host, seeded.PermissionSet, contextResourceId);
        GrantHarnessResource(host, seeded.PermissionSet, channelResourceId);

        var context = new SharpClaw.Contracts.Entities.Core.Context.ChannelContextDB
        {
            Id = Guid.NewGuid(),
            Name = "Default Resource Context",
            AgentId = seeded.Agent.Id,
            Agent = seeded.Agent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        seeded.Channel.AgentContextId = context.Id;
        seeded.Channel.AgentContext = context;
        host.Db.AgentContexts.Add(context);
        await host.Db.SaveChangesAsync();

        var defaults = host.Services.GetRequiredService<DefaultResourceSetService>();
        await defaults.SetKeyForContextAsync(
            context.Id,
            TestHarnessConstants.DefaultResourceKey,
            contextResourceId);
        await defaults.SetKeyForChannelAsync(
            seeded.Channel.Id,
            TestHarnessConstants.DefaultResourceKey,
            channelResourceId);

        var job = await host.Services.GetRequiredService<AgentJobService>().SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobResourceTool,
                ScriptJson: """{"result":"channel-default"}"""));

        job.Status.Should().Be(AgentJobStatus.Completed);
        job.ResourceId.Should().Be(channelResourceId);
        host.Harness.ToolCalls.Should().ContainSingle();
    }

    [Test]
    public async Task DefaultResourceAdapterMapsChannelPermissionSetToNamedSlot()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var channelPermissionSet = PermissionSet();
        var contextPermissionSet = PermissionSet();
        var channelResourceId = Guid.NewGuid();
        var contextResourceId = Guid.NewGuid();
        var roleResourceId = Guid.NewGuid();
        var context = Context(seeded, contextPermissionSet);
        seeded.Channel.PermissionSetId = channelPermissionSet.Id;
        seeded.Channel.AgentContextId = context.Id;
        seeded.Channel.AgentContext = context;
        host.Db.PermissionSets.AddRange(channelPermissionSet, contextPermissionSet);
        host.Db.AgentContexts.Add(context);
        GrantHarnessResource(host, channelPermissionSet, channelResourceId, isDefault: true);
        GrantHarnessResource(host, contextPermissionSet, contextResourceId, isDefault: true);
        GrantHarnessResource(host, seeded.PermissionSet, roleResourceId, isDefault: true);
        await host.Db.SaveChangesAsync();

        var resolved = await ResolveDefaultResourceAsync(host, seeded);

        resolved.Should().Be(channelResourceId);
    }

    [Test]
    public async Task DefaultResourceAdapterMapsContextPermissionSetToNamedSlotWhenChannelSlotMissing()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var contextPermissionSet = PermissionSet();
        var contextResourceId = Guid.NewGuid();
        var roleResourceId = Guid.NewGuid();
        var context = Context(seeded, contextPermissionSet);
        seeded.Channel.AgentContextId = context.Id;
        seeded.Channel.AgentContext = context;
        host.Db.PermissionSets.Add(contextPermissionSet);
        host.Db.AgentContexts.Add(context);
        GrantHarnessResource(host, contextPermissionSet, contextResourceId, isDefault: true);
        GrantHarnessResource(host, seeded.PermissionSet, roleResourceId, isDefault: true);
        await host.Db.SaveChangesAsync();

        var resolved = await ResolveDefaultResourceAsync(host, seeded);

        resolved.Should().Be(contextResourceId);
    }

    [Test]
    public async Task DefaultResourceAdapterMapsRolePermissionSetToNamedSlotWhenChannelAndContextSlotsMissing()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var roleResourceId = Guid.NewGuid();
        GrantHarnessResource(host, seeded.PermissionSet, roleResourceId, isDefault: true);
        await host.Db.SaveChangesAsync();

        var resolved = await ResolveDefaultResourceAsync(host, seeded);

        resolved.Should().Be(roleResourceId);
    }

    [Test]
    public async Task PerResourceJobWithoutDefaultStopsBeforeModuleInvocation()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        GrantHarnessResource(host, seeded.PermissionSet, Guid.NewGuid());
        await host.Db.SaveChangesAsync();

        var job = await host.Services.GetRequiredService<AgentJobService>().SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobResourceTool,
                ScriptJson: """{"result":"should-not-run"}"""));

        job.Status.Should().Be(AgentJobStatus.Denied);
        job.ResourceId.Should().BeNull();
        host.Harness.ToolCalls.Should().BeEmpty();
    }

    private static async Task<Guid?> ResolveDefaultResourceAsync(
        ChatHarnessHost host,
        SeededChat seeded)
    {
        var runtimeHost = (IAgentJobRuntimeHost)host.Services
            .GetRequiredService<AgentJobService>();
        return await runtimeHost.ResolveDefaultResourceIdAsync(
            TestHarnessConstants.JobResourceTool,
            seeded.Channel.Id,
            seeded.Agent.Id,
            CancellationToken.None);
    }

    private static PermissionSetDB PermissionSet() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static SharpClaw.Contracts.Entities.Core.Context.ChannelContextDB Context(
        SeededChat seeded,
        PermissionSetDB permissionSet) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Named Slot Context",
            AgentId = seeded.Agent.Id,
            Agent = seeded.Agent,
            PermissionSetId = permissionSet.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static void GrantHarnessResource(
        ChatHarnessHost host,
        PermissionSetDB permissionSet,
        Guid resourceId,
        bool isDefault = false)
    {
        host.Db.ResourceAccesses.Add(new ResourceAccessDB
        {
            Id = Guid.NewGuid(),
            PermissionSetId = permissionSet.Id,
            ResourceType = TestHarnessConstants.ResourceType,
            ResourceId = resourceId,
            Clearance = PermissionClearance.Independent,
            IsDefault = isDefault,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private sealed class ShadowCoreAgentDefaultModule : ISharpClawCoreModule
    {
        public string Id => "shadow_core_agent_default";
        public string DisplayName => "Shadow Core Agent Default";
        public string ToolPrefix => "shadow";

        public void ConfigureServices(IServiceCollection services) { }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            Task.FromResult("");

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [
            new(
                "Shadow.Agent",
                "ShadowAgent",
                "UseShadowAgentAsync",
                static (_, _) => Task.FromResult(new List<Guid>()),
                DefaultResourceKey: "agent")
        ];
    }
}
