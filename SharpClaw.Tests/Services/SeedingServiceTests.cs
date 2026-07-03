using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Services;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class SeedingServiceTests
{
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
