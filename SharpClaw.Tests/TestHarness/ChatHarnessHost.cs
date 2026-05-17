using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.TestHarness;

namespace SharpClaw.Tests.TestHarness;

internal sealed class ChatHarnessHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    private ChatHarnessHost(ServiceProvider root, AsyncServiceScope scope)
    {
        _root = root;
        _scope = scope;
    }

    public IServiceProvider Services => _scope.ServiceProvider;
    public SharpClawDbContext Db => Services.GetRequiredService<SharpClawDbContext>();
    public ChatService Chat => Services.GetRequiredService<ChatService>();
    public TestHarnessState Harness => _root.GetRequiredService<TestHarnessState>();
    public TestHarnessModule Module => (TestHarnessModule)Services
        .GetRequiredService<ModuleRegistry>()
        .GetModule(TestHarnessConstants.ModuleId)!;
    public CountingPersistenceEntityResolver PersistenceCounter =>
        _root.GetRequiredService<CountingPersistenceEntityResolver>();
    public AsyncServiceScope CreateScope() => _root.CreateAsyncScope();

    public static ChatHarnessHost Create(
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var module = new TestHarnessModule();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = "SharpClawHarness_" + Guid.NewGuid().ToString("N");
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new EncryptionOptions
        {
            Key = new byte[32],
            EncryptProviderKeys = false
        });
        services.AddSingleton(new JwtOptions
        {
            Secret = Convert.ToBase64String(new byte[32])
        });
        services.AddDbContext<SharpClawDbContext>(
            options => options.UseInMemoryDatabase(
                databaseName,
                databaseRoot));
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ModuleMetricsCollector>();
        services.AddSingleton<ThreadActivitySignal>();
        services.AddSingleton<ChatCache>();
        services.AddSingleton<ProviderApiClientFactory>();
        services.AddSingleton<CountingPersistenceEntityResolver>();
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<IPersistenceEntityResolver>(
            sp => sp.GetRequiredService<CountingPersistenceEntityResolver>());
        services.AddScoped<SessionService>();
        services.AddScoped<AgentActionService>();
        services.AddScoped<AgentJobService>();
        services.AddScoped<AgentService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<ContextService>();
        services.AddScoped<DefaultResourceSetService>();
        services.AddScoped<ProviderCostService>();
        services.AddScoped<ProviderService>();
        services.AddScoped<RoleService>();
        services.AddScoped<ThreadService>();
        services.AddScoped<HeaderTagProcessor>();
        services.AddScoped<ChatService>();
        services.AddScoped<ModuleExecutionContext>();
        services.AddScoped<ISharpClawDataContext>(
            sp => sp.GetRequiredService<SharpClawDbContext>());
        services.AddSingleton<ModuleEventDispatcher>(sp => new ModuleEventDispatcher(
            sp,
            sp.GetRequiredService<IConfiguration>(),
            NullLogger<ModuleEventDispatcher>.Instance));

        module.ConfigureServices(services);

        var root = services.BuildServiceProvider();
        var registry = root.GetRequiredService<ModuleRegistry>();
        registry.Register(module);

        return new ChatHarnessHost(root, root.CreateAsyncScope());
    }

    public async Task<SeededChat> SeedChatAsync(
        string providerKey,
        bool grantHarnessPermission = false,
        PermissionClearance clearance = PermissionClearance.Independent,
        string? agentSystemPrompt = "agent system",
        string? customHeader = null,
        bool disableToolSchemas = false,
        bool includeUser = true,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Harness Provider",
            ProviderKey = providerKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        var model = new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = TestHarnessConstants.ModelId,
            ProviderId = provider.Id,
            Provider = provider,
            CapabilityTagsRaw = "chat",
            CreatedAt = now,
            UpdatedAt = now
        };

        var permissionSet = new PermissionSetDB
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        if (grantHarnessPermission)
        {
            permissionSet.GlobalFlags.Add(new GlobalFlagDB
            {
                Id = Guid.NewGuid(),
                FlagKey = TestHarnessConstants.GlobalFlagKey,
                Clearance = clearance,
                PermissionSetId = permissionSet.Id,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = "Harness Role",
            PermissionSetId = permissionSet.Id,
            PermissionSet = permissionSet,
            CreatedAt = now,
            UpdatedAt = now
        };
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Harness Agent",
            ModelId = model.Id,
            Model = model,
            RoleId = role.Id,
            Role = role,
            SystemPrompt = agentSystemPrompt,
            CustomChatHeader = customHeader,
            DisableToolSchemas = disableToolSchemas,
            CreatedAt = now,
            UpdatedAt = now
        };
        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Harness Channel",
            AgentId = agent.Id,
            Agent = agent,
            CreatedAt = now,
            UpdatedAt = now
        };

        UserDB? user = null;
        if (includeUser)
        {
            user = new UserDB
            {
                Id = Guid.NewGuid(),
                Username = "harness-user",
                PasswordHash = [1],
                PasswordSalt = [2],
                RoleId = role.Id,
                Role = role,
                CreatedAt = now,
                UpdatedAt = now
            };
            Db.Users.Add(user);
            Services.GetRequiredService<SessionService>().UserId = user.Id;
        }

        Db.Providers.Add(provider);
        Db.Models.Add(model);
        Db.PermissionSets.Add(permissionSet);
        Db.Roles.Add(role);
        Db.Agents.Add(agent);
        Db.Channels.Add(channel);
        await Db.SaveChangesAsync(ct);

        return new SeededChat(provider, model, agent, channel, role, permissionSet, user);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }
}

internal sealed record SeededChat(
    ProviderDB Provider,
    ModelDB Model,
    AgentDB Agent,
    ChannelDB Channel,
    RoleDB Role,
    PermissionSetDB PermissionSet,
    UserDB? User);

internal sealed class CountingPersistenceEntityResolver : IPersistenceEntityResolver
{
    private readonly EfPersistenceEntityResolver _inner = new();

    public int FindCalls { get; private set; }
    public int QueryCalls { get; private set; }

    public void Reset()
    {
        FindCalls = 0;
        QueryCalls = 0;
    }

    public async Task<T?> FindAsync<T>(
        SharpClawDbContext db,
        Guid id,
        CancellationToken ct = default)
        where T : BaseEntity
    {
        FindCalls++;
        return await _inner.FindAsync<T>(db, id, ct);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
    {
        QueryCalls++;
        return await _inner.QueryAsync(db, predicate, hint, ct);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        SharpClawDbContext db,
        Expression<Func<T, bool>> predicate,
        int limit,
        PersistenceQueryHint? hint = null,
        CancellationToken ct = default)
        where T : BaseEntity
    {
        QueryCalls++;
        return await _inner.QueryAsync(db, predicate, limit, hint, ct);
    }
}

internal static class HarnessBudget
{
    public static void AssertWithin(
        long measuredMs,
        int configuredProviderMs,
        int allowedOverheadMs,
        string surface)
    {
        var budget = configuredProviderMs + allowedOverheadMs;
        measuredMs.Should().BeLessThanOrEqualTo(
            budget,
            $"{surface} has a hard budget of provider time ({configuredProviderMs}ms) plus overhead ({allowedOverheadMs}ms)");
    }

    public static void AssertOverheadPercent(
        long measuredMs,
        int configuredProviderMs,
        int allowedPercent,
        string surface)
    {
        var budget = configuredProviderMs + (configuredProviderMs * allowedPercent / 100);
        measuredMs.Should().BeLessThanOrEqualTo(
            budget,
            $"{surface} has a hard budget of provider time ({configuredProviderMs}ms) plus {allowedPercent}% overhead");
    }

    public static void AssertOverheadAbsolute(
        long measuredMs,
        int configuredProviderMs,
        int allowedOverheadMs,
        string surface)
    {
        measuredMs.Should().BeLessThanOrEqualTo(
            configuredProviderMs + allowedOverheadMs,
            $"{surface} has a hard budget of provider time ({configuredProviderMs}ms) plus {allowedOverheadMs}ms overhead");
    }

    public static void AssertSharpClawOverheadPercent(
        long clientVisibleMs,
        long providerActualMs,
        int configuredProviderMs,
        int allowedPercent,
        string surface)
    {
        var overheadMs = Math.Max(0, clientVisibleMs - providerActualMs);
        var providerJitterMs = providerActualMs - configuredProviderMs;
        var budgetMs = configuredProviderMs * allowedPercent / 100;
        overheadMs.Should().BeLessThanOrEqualTo(
            budgetMs,
            $"{surface} SharpClaw overhead must be within {allowedPercent}% of the configured provider baseline; " +
            $"clientVisibleMs={clientVisibleMs}, providerActualMs={providerActualMs}, " +
            $"providerConfiguredMs={configuredProviderMs}, providerTimerJitterMs={providerJitterMs}, " +
            $"sharpClawOverheadMs={overheadMs}, allowedOverheadMs={budgetMs}");
    }

    public static void AssertSharpClawOverheadAbsolute(
        long clientVisibleMs,
        long providerActualMs,
        int configuredProviderMs,
        int allowedOverheadMs,
        string surface)
    {
        var overheadMs = Math.Max(0, clientVisibleMs - providerActualMs);
        var providerJitterMs = providerActualMs - configuredProviderMs;
        overheadMs.Should().BeLessThanOrEqualTo(
            allowedOverheadMs,
            $"{surface} SharpClaw overhead must stay within {allowedOverheadMs}ms; " +
            $"clientVisibleMs={clientVisibleMs}, providerActualMs={providerActualMs}, " +
            $"providerConfiguredMs={configuredProviderMs}, providerTimerJitterMs={providerJitterMs}, " +
            $"sharpClawOverheadMs={overheadMs}, allowedOverheadMs={allowedOverheadMs}");
    }

    public static void AssertPerCallOverhead(
        double perCallOverheadMs,
        long clientVisibleMs,
        long providerActualMs,
        int calls,
        int maxPerCallMs,
        string surface)
    {
        var overheadMs = Math.Max(0, clientVisibleMs - providerActualMs);
        perCallOverheadMs.Should().BeLessThanOrEqualTo(
            maxPerCallMs,
            $"{surface} per-call SharpClaw overhead must stay under {maxPerCallMs}ms; " +
            $"clientVisibleMs={clientVisibleMs}, providerActualMs={providerActualMs}, " +
            $"sharpClawOverheadMs={overheadMs}, calls={calls}, perCallOverheadMs={perCallOverheadMs:F3}");
    }
}

internal sealed record TimedRunStats(
    IReadOnlyList<long> Measurements,
    long Max,
    double P95,
    double P99)
{
    public static TimedRunStats From(IReadOnlyList<long> measurements)
    {
        measurements.Should().NotBeEmpty();
        var ordered = measurements.Order().ToList();
        return new TimedRunStats(
            measurements,
            ordered[^1],
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99));
    }

    private static double Percentile(IReadOnlyList<long> ordered, double percentile)
    {
        if (ordered.Count == 1)
            return ordered[0];

        var index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        index = Math.Clamp(index, 0, ordered.Count - 1);
        return ordered[index];
    }
}
