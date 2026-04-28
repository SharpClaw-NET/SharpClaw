using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Cli;

public sealed class ChannelCliCommandTests
{
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

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<SessionService>();
        services.AddScoped<ChannelService>();
        services.AddSingleton<ModuleRegistry>();
        return services.BuildServiceProvider();
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
}
