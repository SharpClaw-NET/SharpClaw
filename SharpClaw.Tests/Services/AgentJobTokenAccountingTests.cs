using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Resources;

namespace SharpClaw.Tests.Services;

public sealed class AgentJobTokenAccountingTests
{
    [Test]
    public async Task WhenCurrentExecutionRecordsTokensThenJobResponseIncludesJobCost()
    {
        await using var db = CreateDbContext();
        var job = MakeJob();
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        using (AgentJobService.BeginExecutionScope(job.Id))
        {
            await service.RecordTokensForCurrentExecutionAsync(17, 5);
        }

        var response = await service.GetAsync(job.Id);

        response.Should().NotBeNull();
        response!.JobCost.Should().NotBeNull();
        response.JobCost!.TotalPromptTokens.Should().Be(17);
        response.JobCost.TotalCompletionTokens.Should().Be(5);
        response.JobCost.TotalTokens.Should().Be(22);
    }

    [Test]
    public async Task WhenRecordingTokensForMultipleJobsThenInputOrderGetsTheRemainder()
    {
        await using var db = CreateDbContext();
        var first = MakeJob();
        var second = MakeJob();
        db.AgentJobs.AddRange(first, second);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await service.RecordTokensAsync([second.Id, first.Id], promptTokens: 5, completionTokens: 3);

        var firstResponse = await service.GetAsync(first.Id);
        var secondResponse = await service.GetAsync(second.Id);

        secondResponse!.JobCost!.TotalPromptTokens.Should().Be(3);
        secondResponse.JobCost.TotalCompletionTokens.Should().Be(2);
        firstResponse!.JobCost!.TotalPromptTokens.Should().Be(2);
        firstResponse.JobCost.TotalCompletionTokens.Should().Be(1);
    }

    [Test]
    public async Task WhenModuleCostTrackerRecordsTokensThenJobResponseIncludesJobCost()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        await using var provider = CreateHostProvider(databaseName, root);

        var job = MakeJob();
        using (var setupScope = provider.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            db.AgentJobs.Add(job);
            await db.SaveChangesAsync();
        }

        var tracker = provider.GetRequiredService<IAgentJobCostTracker>();
        await tracker.RecordTokensAsync(job.Id, 23, 9);

        using var verifyScope = provider.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<AgentJobService>();
        var response = await service.GetAsync(job.Id);

        response!.JobCost.Should().NotBeNull();
        response.JobCost!.TotalPromptTokens.Should().Be(23);
        response.JobCost.TotalCompletionTokens.Should().Be(9);
        response.JobCost.TotalTokens.Should().Be(32);
    }

    [Test]
    public void WhenExecutionScopesAreNestedThenPreviousCurrentJobIsRestored()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (AgentJobService.BeginExecutionScope(outer))
        {
            AgentJobService.CurrentExecutionJobId.Should().Be(outer);

            using (AgentJobService.BeginExecutionScope(inner))
            {
                AgentJobService.CurrentExecutionJobId.Should().Be(inner);
            }

            AgentJobService.CurrentExecutionJobId.Should().Be(outer);
        }

        AgentJobService.CurrentExecutionJobId.Should().BeNull();
    }

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;
        return new SharpClawDbContext(options);
    }

    private static AgentJobService CreateService(SharpClawDbContext db)
    {
        var registry = new ModuleRegistry();
        var configuration = new ConfigurationBuilder().Build();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var eventDispatcher = new ModuleEventDispatcher(
            serviceProvider,
            configuration,
            NullLogger<ModuleEventDispatcher>.Instance);

        return new AgentJobService(
            db,
            new EfPersistenceEntityResolver(),
            new AgentActionService(db, registry, new PermissionEvaluationEngine()),
            new SessionService(),
            registry,
            new ModuleMetricsCollector(),
            eventDispatcher,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new ChatCache(configuration),
            new AgentJobLifecycleEngine(),
            new DefaultResourceEngine(),
            NullLogger<AgentJobService>.Instance);
    }

    private static ServiceProvider CreateHostProvider(
        string databaseName,
        InMemoryDatabaseRoot root)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<SharpClawDbContext>(
            options => options.UseInMemoryDatabase(databaseName, root));
        services.AddScoped<IPersistenceEntityResolver, EfPersistenceEntityResolver>();
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleMetricsCollector>();
        services.AddSingleton<ChatCache>();
        services.AddSingleton<PermissionEvaluationEngine>();
        services.AddSingleton<AgentJobLifecycleEngine>();
        services.AddSingleton<DefaultResourceEngine>();
        services.AddSingleton<ModuleEventDispatcher>(sp =>
            new ModuleEventDispatcher(
                sp,
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<ModuleEventDispatcher>.Instance));
        services.AddScoped<AgentActionService>();
        services.AddScoped<SessionService>();
        services.AddScoped<AgentJobService>();
        services.AddSingleton<IAgentJobCostTracker, HostAgentJobCostTracker>();

        return services.BuildServiceProvider();
    }

    private static AgentJobDB MakeJob() => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        Status = AgentJobStatus.Completed,
        ActionKey = "test_action",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
