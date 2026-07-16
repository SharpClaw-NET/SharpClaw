using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ModuleServicePermissionReconciliationTests
{
    [Test]
    public async Task ReconcilePermissionsForModuleAsync_AddsCorePlannedRowsAndSavesOnce()
    {
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var module = new DescriptorModule(
            "permission_reconcile_module",
            resourceTypes: ["existing_resource", "new_resource"],
            flagKeys: ["existing_flag", "new_flag"]);
        var (service, _) = CreateService(db, module);
        var wildcardSet = PermissionSet(
            wildcardResources:
            [
                ("existing_resource", PermissionClearance.Restricted)
            ],
            globalFlags:
            [
                ("existing_flag", PermissionClearance.Unset)
            ]);
        var nonWildcardSet = PermissionSet(
            resourceAccesses:
            [
                ("new_resource", Guid.NewGuid(), PermissionClearance.Independent)
            ]);
        db.PermissionSets.AddRange(wildcardSet, nonWildcardSet);
        await db.SaveChangesAsync();
        saveProbe.Reset();

        await InvokeReconcileAsync(service, module);

        saveProbe.SaveCount.Should().Be(1);
        var savedWildcardSet = await LoadPermissionSetAsync(db, wildcardSet.Id);
        savedWildcardSet.ResourceAccesses.Should().Contain(access =>
            access.ResourceType == "existing_resource"
            && access.ResourceId == WellKnownIds.AllResources
            && access.Clearance == PermissionClearance.Restricted);
        savedWildcardSet.ResourceAccesses.Should().Contain(access =>
            access.ResourceType == "new_resource"
            && access.ResourceId == WellKnownIds.AllResources
            && access.Clearance == PermissionClearance.Independent);
        savedWildcardSet.GlobalFlags.Should().Contain(flag =>
            flag.FlagKey == "existing_flag"
            && flag.Clearance == PermissionClearance.Unset);
        savedWildcardSet.GlobalFlags.Should().Contain(flag =>
            flag.FlagKey == "new_flag"
            && flag.Clearance == PermissionClearance.Independent);

        var savedNonWildcardSet = await LoadPermissionSetAsync(db, nonWildcardSet.Id);
        savedNonWildcardSet.ResourceAccesses.Should().ContainSingle();
        savedNonWildcardSet.GlobalFlags.Should().BeEmpty();
    }

    [Test]
    public async Task ReconcilePermissionsForModuleAsync_WhenNoPlanChanges_DoesNotSave()
    {
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var module = new DescriptorModule(
            "permission_reconcile_noop_module",
            resourceTypes: ["existing_resource"],
            flagKeys: ["existing_flag"]);
        var (service, _) = CreateService(db, module);
        db.PermissionSets.Add(PermissionSet(
            wildcardResources:
            [
                ("existing_resource", PermissionClearance.Unset)
            ],
            globalFlags:
            [
                ("existing_flag", PermissionClearance.Restricted)
            ]));
        await db.SaveChangesAsync();
        saveProbe.Reset();

        await InvokeReconcileAsync(service, module);

        saveProbe.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task EnableAsync_ForNewModuleStillBackfillsPermissionsAndPersistsEnabled()
    {
        var saveProbe = new SaveProbe();
        await using var db = CreateDbContext(saveProbe);
        var module = new DescriptorModule(
            "permission_reconcile_enable_module",
            resourceTypes: ["existing_resource", "enabled_resource"],
            flagKeys: ["enabled_flag"]);
        var wildcardSet = PermissionSet(
            wildcardResources:
            [
                ("existing_resource", PermissionClearance.Independent)
            ]);
        db.PermissionSets.Add(wildcardSet);
        await db.SaveChangesAsync();
        saveProbe.Reset();
        var (service, rootServices) = CreateService(db, module);

        var response = await service.EnableAsync(module.Id, rootServices);

        response.ModuleId.Should().Be(module.Id);
        response.Enabled.Should().BeTrue();
        module.InitializeCallCount.Should().Be(1);
        db.ModuleStates.AsNoTracking()
            .Should()
            .ContainSingle(state => state.ModuleId == module.Id && state.Enabled);
        var saved = await LoadPermissionSetAsync(db, wildcardSet.Id);
        saved.ResourceAccesses.Should().Contain(access =>
            access.ResourceType == "enabled_resource"
            && access.ResourceId == WellKnownIds.AllResources
            && access.Clearance == PermissionClearance.Independent);
        saved.GlobalFlags.Should().Contain(flag =>
            flag.FlagKey == "enabled_flag"
            && flag.Clearance == PermissionClearance.Independent);
        saveProbe.SaveCount.Should().BeGreaterThan(0);
    }

    private static async Task InvokeReconcileAsync(
        ModuleService service,
        ISharpClawCoreModule module)
    {
        var method = typeof(ModuleService).GetMethod(
            "ReconcilePermissionsForModuleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var task = (Task)method!.Invoke(
            service,
            [module, CancellationToken.None])!;
        await task;
    }

    private static async Task<PermissionSetDB> LoadPermissionSetAsync(
        SharpClawDbContext db,
        Guid permissionSetId) =>
        await db.PermissionSets
            .Include(set => set.ResourceAccesses)
            .Include(set => set.GlobalFlags)
            .AsSplitQuery()
            .SingleAsync(set => set.Id == permissionSetId);

    private static PermissionSetDB PermissionSet(
        IReadOnlyList<(string ResourceType, PermissionClearance Clearance)>? wildcardResources = null,
        IReadOnlyList<(string ResourceType, Guid ResourceId, PermissionClearance Clearance)>? resourceAccesses = null,
        IReadOnlyList<(string FlagKey, PermissionClearance Clearance)>? globalFlags = null)
    {
        var permissionSet = new PermissionSetDB
        {
            Id = Guid.NewGuid()
        };

        foreach (var (resourceType, clearance) in wildcardResources ?? [])
        {
            permissionSet.ResourceAccesses.Add(new ResourceAccessDB
            {
                Id = Guid.NewGuid(),
                PermissionSetId = permissionSet.Id,
                ResourceType = resourceType,
                ResourceId = WellKnownIds.AllResources,
                Clearance = clearance,
            });
        }

        foreach (var (resourceType, resourceId, clearance) in resourceAccesses ?? [])
        {
            permissionSet.ResourceAccesses.Add(new ResourceAccessDB
            {
                Id = Guid.NewGuid(),
                PermissionSetId = permissionSet.Id,
                ResourceType = resourceType,
                ResourceId = resourceId,
                Clearance = clearance,
            });
        }

        foreach (var (flagKey, clearance) in globalFlags ?? [])
        {
            permissionSet.GlobalFlags.Add(new GlobalFlagDB
            {
                Id = Guid.NewGuid(),
                PermissionSetId = permissionSet.Id,
                FlagKey = flagKey,
                Clearance = clearance,
            });
        }

        return permissionSet;
    }

    private static SharpClawDbContext CreateDbContext(SaveProbe saveProbe)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(
                "ModulePermissionReconciliation_" + Guid.NewGuid().ToString("N"),
                new InMemoryDatabaseRoot())
            .AddInterceptors(saveProbe)
            .Options;

        return new SharpClawDbContext(options);
    }

    private static (ModuleService Service, IServiceProvider RootServices)
        CreateService(SharpClawDbContext db, ISharpClawCoreModule module)
    {
        var configuration = new ConfigurationBuilder().Build();
        var registry = new ModuleRegistry();
        var rootServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(registry)
            .BuildServiceProvider();
        var service = new ModuleService(
            db,
            new ModuleLoader(module),
            registry,
            new RuntimeModuleDbContextRegistry(),
            new ModulePersistenceRegistrationFactory(),
            new ModuleEventDispatcher(
                rootServices,
                configuration,
                NullLogger<ModuleEventDispatcher>.Instance),
            NullLogger<ModuleService>.Instance,
            new ChatCache(configuration),
            new UnusedDocumentUpdater(),
            configuration);

        return (service, rootServices);
    }

    private sealed class UnusedDocumentUpdater : ISecretDocumentUpdater
    {
        public Task UpdateDocumentAsync(
            Func<IReadOnlyList<SupprocomSecretSetting>, IReadOnlyList<SupprocomSecretSetting>> update,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This permission-reconciliation fixture does not load external modules.");
    }

    private sealed class DescriptorModule(
        string id,
        IReadOnlyList<string> resourceTypes,
        IReadOnlyList<string> flagKeys) : ISharpClawCoreModule
    {
        public string Id => id;
        public string DisplayName => id;
        public string ToolPrefix => "permrecon";
        public int InitializeCallCount { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
            resourceTypes
                .Select(resourceType => new ModuleResourceTypeDescriptor(
                    resourceType,
                    resourceType,
                    resourceType + "Async",
                    (_, _) => Task.FromResult(new List<Guid>())))
                .ToList();

        public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
            flagKeys
                .Select(flagKey => new ModuleGlobalFlagDescriptor(
                    flagKey,
                    flagKey,
                    flagKey,
                    flagKey + "Async"))
                .ToList();

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct)
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public int SaveCount { get; private set; }

        public void Reset() => SaveCount = 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
