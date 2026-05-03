using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Cli;

public sealed class ChannelCliCommandTests
{
    [Test]
    public async Task WhenContextDefaultsSetUsesUnknownKeyAndInvalidResourceValueThenKeyErrorIsReported()
    {
        var services = CreateServices(registerDefaultResourceModule: true);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (_, _, contextId, _) = await SeedChannelGraphAsync(db);

        var error = await CaptureErrorAsync(() => CliDispatcher.TryHandleAsync(
            ["context", "defaults", contextId.ToString(), "set", "unknown", "all"],
            services));

        error.Should().Contain("Unknown key 'unknown'.");
        error.Should().NotContain("Unrecognized Guid format");
    }

    [Test]
    public async Task WhenChannelDefaultsSetUsesUnknownKeyAndInvalidResourceValueThenKeyErrorIsReported()
    {
        var services = CreateServices(registerDefaultResourceModule: true);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (channelId, _, _, _) = await SeedChannelGraphAsync(db);

        var error = await CaptureErrorAsync(() => CliDispatcher.TryHandleAsync(
            ["channel", "defaults", channelId.ToString(), "set", "unknown", "all"],
            services));

        error.Should().Contain("Unknown key 'unknown'.");
        error.Should().NotContain("Unrecognized Guid format");
    }

    [Test]
    public async Task WhenContextDefaultsAreEmptyThenPlaceholderIdIsNotPrinted()
    {
        var services = CreateServices();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (_, _, contextId, _) = await SeedChannelGraphAsync(db);

        var output = await CaptureOutputAsync(() => CliDispatcher.TryHandleAsync(
            ["context", "defaults", contextId.ToString()],
            services));

        output.Should().Contain("(no defaults set)");
        output.Should().NotContain(Guid.Empty.ToString());
    }

    [Test]
    public async Task WhenChannelDefaultsAreEmptyThenPlaceholderIdIsNotPrinted()
    {
        var services = CreateServices();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (channelId, _, _, _) = await SeedChannelGraphAsync(db);

        var output = await CaptureOutputAsync(() => CliDispatcher.TryHandleAsync(
            ["channel", "defaults", channelId.ToString()],
            services));

        output.Should().Contain("(no defaults set)");
        output.Should().NotContain(Guid.Empty.ToString());
    }

    [Test]
    public async Task WhenContextDefaultsSetUsesKnownKeyAndResourceIdThenDefaultIsStored()
    {
        var services = CreateServices(registerDefaultResourceModule: true);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (_, agentId, contextId, _) = await SeedChannelGraphAsync(db);

        var handled = await CliDispatcher.TryHandleAsync(
            ["context", "defaults", contextId.ToString(), "set", "agent", agentId.ToString()],
            services);

        handled.Should().BeTrue();
        await using var assertScope = services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var context = await assertDb.AgentContexts
            .Include(c => c.DefaultResourceSet!).ThenInclude(d => d.Entries)
            .SingleAsync(c => c.Id == contextId);
        context.DefaultResourceSetId.Should().NotBeNull();
        context.DefaultResourceSet!.Id.Should().NotBe(Guid.Empty);
        context.DefaultResourceSet.Entries.Should().ContainSingle(e =>
            e.ResourceKey == "agent" && e.ResourceId == agentId);
    }

    [Test]
    public async Task WhenChannelUpdateSuppliesMutableFieldsThenChannelIsUpdated()
    {
        var services = CreateServices();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (channelId, agentId, contextId, toolSetId) = await SeedChannelGraphAsync(db);

        var handled = await CliDispatcher.TryHandleAsync(
            [
                "channel",
                "update",
                channelId.ToString(),
                "--title",
                "Updated title",
                "--agent",
                agentId.ToString(),
                "--context",
                contextId.ToString(),
                "--header",
                "Updated header",
                "--tools",
                toolSetId.ToString(),
                "--no-tools",
            ],
            services);

        handled.Should().BeTrue();
        await using var assertScope = services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var channel = await assertDb.Channels.SingleAsync(c => c.Id == channelId);
        channel.Title.Should().Be("Updated title");
        channel.AgentId.Should().Be(agentId);
        channel.AgentContextId.Should().Be(contextId);
        channel.CustomChatHeader.Should().Be("Updated header");
        channel.ToolAwarenessSetId.Should().Be(toolSetId);
        channel.DisableToolSchemas.Should().BeTrue();
    }

    [Test]
    public async Task WhenChannelUpdateClearsNullableFieldsThenChannelOverridesAreRemoved()
    {
        var services = CreateServices();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var (channelId, _, contextId, toolSetId) = await SeedChannelGraphAsync(db);

        var channel = await db.Channels.SingleAsync(c => c.Id == channelId);
        channel.AgentContextId = contextId;
        channel.CustomChatHeader = "Header";
        channel.ToolAwarenessSetId = toolSetId;
        channel.DisableToolSchemas = true;
        await db.SaveChangesAsync();

        var handled = await CliDispatcher.TryHandleAsync(
            [
                "channel",
                "update",
                channelId.ToString(),
                "--context",
                "none",
                "--header",
                "none",
                "--tools",
                "none",
                "--enable-tools",
            ],
            services);

        handled.Should().BeTrue();
        await using var assertScope = services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        channel = await assertDb.Channels.SingleAsync(c => c.Id == channelId);
        channel.AgentContextId.Should().BeNull();
        channel.CustomChatHeader.Should().BeNull();
        channel.ToolAwarenessSetId.Should().BeNull();
        channel.DisableToolSchemas.Should().BeFalse();
    }

    private static ServiceProvider CreateServices(bool registerDefaultResourceModule = false)
    {
        var services = new ServiceCollection();
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<SessionService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<ContextService>();
        services.AddScoped<DefaultResourceSetService>();
        services.AddSingleton<ModuleRegistry>();
        var provider = services.BuildServiceProvider();

        if (registerDefaultResourceModule)
            provider.GetRequiredService<ModuleRegistry>().Register(new TestDefaultResourceModule());

        return provider;
    }

    private static async Task<string> CaptureOutputAsync(Func<Task<bool>> action)
    {
        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static async Task<string> CaptureErrorAsync(Func<Task<bool>> action)
    {
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task<(Guid ChannelId, Guid AgentId, Guid ContextId, Guid ToolSetId)> SeedChannelGraphAsync(
        SharpClawDbContext db)
    {
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Provider",
            ProviderKey = WellKnownProviderKeys.OpenAI,
        };
        var model = new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = "gpt-test",
            Provider = provider,
            ProviderId = provider.Id,
        };
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Agent",
            Model = model,
            ModelId = model.Id,
        };
        var context = new ChannelContextDB
        {
            Id = Guid.NewGuid(),
            Name = "Context",
            Agent = agent,
            AgentId = agent.Id,
        };
        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Original title",
            Agent = agent,
            AgentId = agent.Id,
        };
        var toolSet = new ToolAwarenessSetDB
        {
            Id = Guid.NewGuid(),
            Name = "Tools",
            Tools = [],
        };

        db.Providers.Add(provider);
        db.Models.Add(model);
        db.Agents.Add(agent);
        db.AgentContexts.Add(context);
        db.Channels.Add(channel);
        db.ToolAwarenessSets.Add(toolSet);
        await db.SaveChangesAsync();

        return (channel.Id, agent.Id, context.Id, toolSet.Id);
    }

    private sealed class TestDefaultResourceModule : ISharpClawModule
    {
        public string Id => "test_default_resources";

        public string DisplayName => "Test Default Resources";

        public string ToolPrefix => "testdefaults";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        Task<string> ISharpClawModule.ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            SharpClaw.Contracts.Modules.AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) => throw new NotSupportedException();

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [
            new(
                "TestAgent",
                "TestAgent",
                "TestAgentAsync",
                static (_, _) => Task.FromResult(new List<Guid>()),
                DefaultResourceKey: "agent"),
        ];
    }
}
