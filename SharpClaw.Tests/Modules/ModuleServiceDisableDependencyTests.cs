using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Modules.Foreign;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.Modules;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ModuleServiceDisableDependencyTests
{
    [Test]
    public void EvaluateDisableDependencies_CollectsModuleAndProtocolContracts()
    {
        var target = new TestModule(
            "target_module",
            "target",
            exportedContracts:
            [
                new ModuleContractExport("module_contract", typeof(IDisposable))
            ],
            exportedProtocolContracts:
            [
                new ForeignModuleProtocolContractExport(
                    "protocol_contract",
                    EmptySchema(),
                    [])
            ]);
        var dependent = new TestModule(
            "dependent_module",
            "depend",
            requiredContracts:
            [
                new ModuleContractRequirement("module_contract")
            ],
            requiredProtocolContracts:
            [
                new ForeignModuleProtocolContractRequirement("protocol_contract")
            ]);
        var registry = new ModuleRegistry();
        registry.Register(target);
        registry.Register(dependent);

        var decision = ModuleService.EvaluateDisableDependencies(
            target.Id,
            target,
            registry);

        decision.CanDisable.Should().BeFalse();
        decision.BlockerModuleId.Should().Be(dependent.Id);
        decision.BlockingContracts.Should().Equal(
            "module_contract",
            "protocol_contract");
    }

    [Test]
    public async Task DisableAsync_WhenDependencyBlocks_ThrowsLegacyAppMessageBeforeMutation()
    {
        await using var db = CreateDbContext();
        var target = new TestModule(
            "target_module",
            "target",
            exportedContracts:
            [
                new ModuleContractExport("module_contract", typeof(IDisposable))
            ]);
        var dependent = new TestModule(
            "dependent_module",
            "depend",
            requiredContracts:
            [
                new ModuleContractRequirement("module_contract")
            ]);
        var registry = new ModuleRegistry();
        registry.Register(target);
        registry.Register(dependent);
        db.ModuleStates.Add(new ModuleStateDB
        {
            ModuleId = target.Id,
            Enabled = true,
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var configuration = CreateConfiguration();
        using var rootServices = CreateRootServices(registry, configuration);
        var service = CreateService(
            db,
            new ModuleLoader(target),
            registry,
            rootServices,
            configuration);

        var act = () => service.DisableAsync(target.Id);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "Cannot disable 'target_module': module 'dependent_module' depends on contract(s) module_contract.");
        target.ShutdownCallCount.Should().Be(0);
        registry.GetModule(target.Id).Should().BeSameAs(target);
        var persisted = await db.ModuleStates
            .AsNoTracking()
            .SingleAsync(s => s.ModuleId == target.Id);
        persisted.Enabled.Should().BeTrue();
    }

    [Test]
    public async Task DisableAsync_WhenDependencyAllows_ShutsDownUnregistersAndPersistsDisabled()
    {
        await using var db = CreateDbContext();
        var target = new TestModule(
            "target_module",
            "target",
            exportedContracts:
            [
                new ModuleContractExport("module_contract", typeof(IDisposable))
            ]);
        var optionalDependent = new TestModule(
            "optional_module",
            "optional",
            requiredContracts:
            [
                new ModuleContractRequirement(
                    "module_contract",
                    Optional: true)
            ]);
        var registry = new ModuleRegistry();
        registry.Register(target);
        registry.Register(optionalDependent);
        db.ModuleStates.Add(new ModuleStateDB
        {
            ModuleId = target.Id,
            Enabled = true,
            Version = "1.0.0"
        });
        await db.SaveChangesAsync();

        var configuration = CreateConfiguration();
        using var rootServices = CreateRootServices(registry, configuration);
        var service = CreateService(
            db,
            new ModuleLoader(target),
            registry,
            rootServices,
            configuration);

        var response = await service.DisableAsync(target.Id);

        response.Enabled.Should().BeFalse();
        target.ShutdownCallCount.Should().Be(1);
        registry.GetModule(target.Id).Should().BeNull();
        registry.GetModule(optionalDependent.Id).Should().BeSameAs(optionalDependent);
        var persisted = await db.ModuleStates
            .AsNoTracking()
            .SingleAsync(s => s.ModuleId == target.Id);
        persisted.Enabled.Should().BeFalse();
    }

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(
                "ModuleDisableDependency_" + Guid.NewGuid().ToString("N"),
                new InMemoryDatabaseRoot())
            .Options;

        return new SharpClawDbContext(options);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().Build();

    private static ServiceProvider CreateRootServices(
        ModuleRegistry registry,
        IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(registry)
            .BuildServiceProvider();

    private static ModuleService CreateService(
        SharpClawDbContext db,
        ModuleLoader loader,
        ModuleRegistry registry,
        IServiceProvider rootServices,
        IConfiguration configuration) =>
        new(
            db,
            loader,
            registry,
            new RuntimeModuleDbContextRegistry(),
            new ModulePersistenceRegistrationFactory(),
            new ModuleEventDispatcher(
                rootServices,
                configuration,
                NullLogger<ModuleEventDispatcher>.Instance),
            NullLogger<ModuleService>.Instance,
            new ChatCache(configuration),
            configuration);

    private static JsonElement EmptySchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }

    private sealed class TestModule(
        string id,
        string toolPrefix,
        IReadOnlyList<ModuleContractExport>? exportedContracts = null,
        IReadOnlyList<ModuleContractRequirement>? requiredContracts = null,
        IReadOnlyList<ForeignModuleProtocolContractExport>? exportedProtocolContracts = null,
        IReadOnlyList<ForeignModuleProtocolContractRequirement>? requiredProtocolContracts = null)
        : ISharpClawCoreModule, IForeignModuleProtocolContractExporter
    {
        public string Id => id;
        public string DisplayName => id;
        public string ToolPrefix => toolPrefix;
        public int ShutdownCallCount { get; private set; }
        public IReadOnlyList<ModuleContractExport> ExportedContracts =>
            exportedContracts ?? [];
        public IReadOnlyList<ModuleContractRequirement> RequiredContracts =>
            requiredContracts ?? [];
        public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts =>
            exportedProtocolContracts ?? [];
        public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts =>
            requiredProtocolContracts ?? [];

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ShutdownAsync()
        {
            ShutdownCallCount++;
            return Task.CompletedTask;
        }

        public IForeignModuleProtocolContractInvoker GetProtocolContractInvoker(
            string contractName) =>
            new TestProtocolInvoker(contractName);
    }

    private sealed class TestProtocolInvoker(string contractName)
        : IForeignModuleProtocolContractInvoker
    {
        public string ContractName => contractName;
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations => [];

        public Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
