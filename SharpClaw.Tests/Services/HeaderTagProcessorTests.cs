using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class HeaderTagProcessorTests
{
    [Test]
    public async Task ExpandAsync_WhenHeaderTagExpansionIsDisabled_ReturnsTemplateUnchanged()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, disableHeaderTagExpansion: true);
        var channel = new ChannelDB { Title = "Channel" };
        var agent = new AgentDB { Name = "Agent" };
        const string template = "[{{time}} | {{agent-name}} | {{Agents:{Name}}}]";

        var expanded = await processor.ExpandAsync(
            template,
            channel,
            agent,
            "api",
            userId: null,
            CancellationToken.None);

        expanded.Should().Be(template);
    }

    [Test]
    public async Task ExpandAsync_WhenHeaderTagExpansionIsEnabled_ExpandsBuiltInTags()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, disableHeaderTagExpansion: false);
        var channel = new ChannelDB { Title = "Channel" };
        var agent = new AgentDB { Name = "Agent" };

        var expanded = await processor.ExpandAsync(
            "[{{agent-name}}]",
            channel,
            agent,
            "api",
            userId: null,
            CancellationToken.None);

        expanded.Should().Be("[Agent]");
    }

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new SharpClawDbContext(options);
    }

    private static HeaderTagProcessor CreateProcessor(
        SharpClawDbContext db,
        bool disableHeaderTagExpansion)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:DisableHeaderTagExpansion"] = disableHeaderTagExpansion.ToString()
            })
            .Build();

        var registry = new ModuleRegistry();
        var services = new ServiceCollection().BuildServiceProvider();
        var clientFactory = new ProviderApiClientFactory(
            Array.Empty<IProviderPlugin>(),
            registry);

        return new HeaderTagProcessor(
            db,
            registry,
            services,
            clientFactory,
            configuration);
    }
}
