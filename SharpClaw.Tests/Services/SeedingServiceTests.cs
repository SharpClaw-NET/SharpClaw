using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class SeedingServiceTests
{
    [Test]
    public void IsJsonColdStoreWriterLockConflict_WhenProviderLockMessageIsNested_ReturnsTrue()
    {
        var ex = new InvalidOperationException(
            "Save failed.",
            new InvalidOperationException(
                "The JSONColdStore database writer lock is already held by process 2172."));

        SeedingService.IsJsonColdStoreWriterLockConflict(ex).Should().BeTrue();
    }

    [Test]
    public void IsJsonColdStoreWriterLockConflict_WhenUnrelatedInvalidOperation_ReturnsFalse()
    {
        var ex = new InvalidOperationException("The provider rejected this query.");

        SeedingService.IsJsonColdStoreWriterLockConflict(ex).Should().BeFalse();
    }

    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameIsMissing_FailsStartup()
    {
        await using var provider = CreateAnonymousUserProvider(
            new Dictionary<string, string?>
            {
                ["Auth:AnonymousUsername"] = "missing-user"
            });
        var service = provider.GetRequiredService<SeedingService>();

        var act = async () => await service.StartingAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Auth:AnonymousUsername*missing-user*no matching user exists*");
    }

    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameIsEmpty_FailsStartup()
    {
        await using var provider = CreateAnonymousUserProvider(
            new Dictionary<string, string?>
            {
                ["Auth:AnonymousUsername"] = ""
            });
        var service = provider.GetRequiredService<SeedingService>();

        var act = async () => await service.StartingAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Auth:AnonymousUsername*empty*");
    }

    [Test]
    public async Task StartingAsync_WhenAnonymousUsernameExists_StartsSuccessfully()
    {
        await using var provider = CreateAnonymousUserProvider(
            new Dictionary<string, string?>
            {
                ["Auth:AnonymousUsername"] = "anonymous"
            });
        await SeedUserAsync(provider, "anonymous", isAdmin: false);
        var service = provider.GetRequiredService<SeedingService>();

        await service.StartingAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        (await db.Users.AnyAsync(user => user.Username == "anonymous")).Should().BeTrue();
        (await db.Users.AnyAsync(user => user.Username == "admin" && user.IsUserAdmin))
            .Should().BeTrue();
    }

    [Test]
    public async Task StartingAsync_WhenReconcileEnabled_AppliesCorePermissionPlanToEfRows()
    {
        var registry = new ModuleRegistry();
        registry.Register(new SeedPermissionModule());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:ReconcilePermissions"] = "true"
            })
            .Build();
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(registry)
            .AddSingleton(new ProviderApiClientFactory([], registry))
            .AddDbContext<SharpClawDbContext>(options =>
                options.UseInMemoryDatabase(databaseName, databaseRoot));

        await using var provider = services.BuildServiceProvider();
        Guid permissionSetId;
        var specificResourceId = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var permissionSet = new PermissionSetDB();
            permissionSet.GlobalFlags.Add(new GlobalFlagDB
            {
                FlagKey = "CanSeedModule",
                Clearance = PermissionClearance.Unset
            });
            permissionSet.ResourceAccesses.Add(new ResourceAccessDB
            {
                ResourceType = "seed_resource",
                ResourceId = specificResourceId,
                Clearance = PermissionClearance.Restricted
            });
            db.PermissionSets.Add(permissionSet);
            await db.SaveChangesAsync();
            permissionSetId = permissionSet.Id;
            db.Roles.Add(new RoleDB
            {
                Name = "Admin",
                PermissionSetId = permissionSetId
            });
            await db.SaveChangesAsync();
        }

        var seeding = new SeedingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<SeedingService>.Instance,
            registry,
            new AdminPermissionSeedEngine());

        await seeding.StartingAsync(CancellationToken.None);

        using var verifyScope = provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var saved = await verifyDb.PermissionSets
            .Include(set => set.GlobalFlags)
            .Include(set => set.ResourceAccesses)
            .SingleAsync(set => set.Id == permissionSetId);

        saved.GlobalFlags.Single(flag => flag.FlagKey == "CanSeedModule")
            .Clearance.Should().Be(PermissionClearance.Independent);
        saved.GlobalFlags.Single(flag => flag.FlagKey == "CanSeedExtra")
            .Clearance.Should().Be(PermissionClearance.Independent);
        saved.ResourceAccesses.Single(access =>
            access.ResourceType == "seed_resource"
            && access.ResourceId == WellKnownIds.AllResources)
            .Clearance.Should().Be(PermissionClearance.Independent);
        saved.ResourceAccesses.Single(access =>
            access.ResourceType == "seed_resource"
            && access.ResourceId == specificResourceId)
            .Clearance.Should().Be(PermissionClearance.Restricted);
        (await verifyDb.Users.CountAsync()).Should().Be(0);
        (await verifyDb.Providers.CountAsync()).Should().Be(0);
    }

    private static ServiceProvider CreateAnonymousUserProvider(
        IReadOnlyDictionary<string, string?> configurationOverrides)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["Admin:Username"] = "admin",
            ["Admin:Password"] = "123456",
            ["Admin:ReconcilePermissions"] = "true"
        };
        foreach (var pair in configurationOverrides)
            configurationValues[pair.Key] = pair.Value;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var databaseName = $"SeedingServiceTests-{Guid.NewGuid():N}";
        var registry = new ModuleRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(registry);
        services.AddSingleton(new ProviderApiClientFactory([], registry));
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<SeedingService>(provider =>
            new SeedingService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<SeedingService>.Instance,
                registry,
                new AdminPermissionSeedEngine()));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedUserAsync(
        ServiceProvider provider,
        string username,
        bool isAdmin)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        db.Users.Add(new UserDB
        {
            Username = username,
            PasswordHash = [1],
            PasswordSalt = [2],
            IsUserAdmin = isAdmin
        });
        await db.SaveChangesAsync();
    }

    private sealed class SeedPermissionModule : ISharpClawCoreModule
    {
        public string Id => "seed_permissions";
        public string DisplayName => "Seed Permissions";
        public string ToolPrefix => "seedperm";
        public void ConfigureServices(IServiceCollection services) { }
        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
        [
            new(
                "CanSeedModule",
                "Seed Module",
                "Seed module permissions.",
                "SeedModuleAsync"),
            new(
                "CanSeedExtra",
                "Seed Extra",
                "Seed extra permissions.",
                "SeedExtraAsync")
        ];

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [
            new(
                "seed_resource",
                "Seed Resource",
                "UseSeedResourceAsync",
                (_, _) => Task.FromResult(new List<Guid>()))
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            Task.FromResult("");
    }
}
