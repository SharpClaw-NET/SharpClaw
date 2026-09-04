using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;
using SharpClaw.Contracts.Kernel.Foreign;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class RegistrationServiceDisableDependencyTests
{
    [Test]
    public void EvaluateDisableDependencies_CollectsRegistrationAndProtocolContracts()
    {
        var target = new TestRegistration(
            "target_registration",
            "target",
            exportedContracts:
            [
                new ContractExport("registration_contract", typeof(IDisposable))
            ],
            exportedProtocolContracts:
            [
                new ForeignRegistrationProtocolContractExport(
                    "protocol_contract",
                    EmptySchema(),
                    [])
            ]);
        var dependent = new TestRegistration(
            "dependent_registration",
            "depend",
            requiredContracts:
            [
                new ContractRequirement("registration_contract")
            ],
            requiredProtocolContracts:
            [
                new ForeignRegistrationProtocolContractRequirement("protocol_contract")
            ]);
        var registry = new RegistrationCatalog();
        registry.Register(target);
        registry.Register(dependent);

        var decision = RegistrationService.EvaluateDisableDependencies(
            target.Id,
            target,
            registry);

        decision.CanDisable.Should().BeFalse();
        decision.BlockerRegistrationId.Should().Be(dependent.Id);
        decision.BlockingContracts.Should().Equal(
            "registration_contract",
            "protocol_contract");
    }

    [Test]
    public async Task DisableAsync_WhenDependencyBlocks_ThrowsLegacyAppMessageBeforeMutation()
    {
        await using var db = CreateDbContext();
        var target = new TestRegistration(
            "target_registration",
            "target",
            exportedContracts:
            [
                new ContractExport("registration_contract", typeof(IDisposable))
            ]);
        var dependent = new TestRegistration(
            "dependent_registration",
            "depend",
            requiredContracts:
            [
                new ContractRequirement("registration_contract")
            ]);
        var registry = new RegistrationCatalog();
        registry.Register(target);
        registry.Register(dependent);
        db.RegistrationStates.Add(new RegistrationStateDB
        {
            SourceId = target.Id,
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
                "Cannot disable 'target_registration': module 'dependent_registration' depends on contract(s) registration_contract.");
        target.ShutdownCallCount.Should().Be(0);
        registry.GetRegistration(target.Id).Should().BeSameAs(target);
        var persisted = await db.RegistrationStates
            .AsNoTracking()
            .SingleAsync(s => s.SourceId == target.Id);
        persisted.Enabled.Should().BeTrue();
    }

    [Test]
    public async Task DisableAsync_WhenDependencyAllows_ShutsDownUnregistersAndPersistsDisabled()
    {
        await using var db = CreateDbContext();
        var target = new TestRegistration(
            "target_registration",
            "target",
            exportedContracts:
            [
                new ContractExport("registration_contract", typeof(IDisposable))
            ]);
        var optionalDependent = new TestRegistration(
            "optional_registration",
            "optional",
            requiredContracts:
            [
                new ContractRequirement(
                    "registration_contract",
                    Optional: true)
            ]);
        var registry = new RegistrationCatalog();
        registry.Register(target);
        registry.Register(optionalDependent);
        db.RegistrationStates.Add(new RegistrationStateDB
        {
            SourceId = target.Id,
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
        registry.GetRegistration(target.Id).Should().BeNull();
        registry.GetRegistration(optionalDependent.Id).Should().BeSameAs(optionalDependent);
        var persisted = await db.RegistrationStates
            .AsNoTracking()
            .SingleAsync(s => s.SourceId == target.Id);
        persisted.Enabled.Should().BeFalse();
    }

    private static SharpClawDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(
                "RegistrationDisableDependency_" + Guid.NewGuid().ToString("N"),
                new InMemoryDatabaseRoot())
            .Options;

        return new SharpClawDbContext(options);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().Build();

    private static ServiceProvider CreateRootServices(
        RegistrationCatalog registry,
        IConfiguration configuration) =>
        new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(registry)
            .BuildServiceProvider();

    private static RegistrationService CreateService(
        SharpClawDbContext db,
        ModuleLoader loader,
        RegistrationCatalog registry,
        IServiceProvider rootServices,
        IConfiguration configuration) =>
        new(
            db,
            loader,
            registry,
            new RuntimeRegistrationDbContextRegistry(),
            new RegistrationPersistenceRegistrationFactory(),
            new RegistrationEventDispatcher(
                rootServices,
                configuration,
                NullLogger<RegistrationEventDispatcher>.Instance),
            NullLogger<RegistrationService>.Instance,
            new ChatCache(configuration),
            new UnusedDocumentUpdater(),
            configuration);

    private sealed class UnusedDocumentUpdater : ISecretDocumentUpdater
    {
        public Task UpdateDocumentAsync(
            Func<IReadOnlyList<SupprocomSecretSetting>, IReadOnlyList<SupprocomSecretSetting>> update,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This disable-dependency fixture does not load external modules.");
    }

    private static JsonElement EmptySchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }

    private sealed class TestRegistration(
        string id,
        string toolPrefix,
        IReadOnlyList<ContractExport>? exportedContracts = null,
        IReadOnlyList<ContractRequirement>? requiredContracts = null,
        IReadOnlyList<ForeignRegistrationProtocolContractExport>? exportedProtocolContracts = null,
        IReadOnlyList<ForeignRegistrationProtocolContractRequirement>? requiredProtocolContracts = null)
        : ISharpClawCoreRegistration, IForeignRegistrationProtocolContractExporter
    {
        public string Id => id;
        public string DisplayName => id;
        public string ToolPrefix => toolPrefix;
        public int ShutdownCallCount { get; private set; }
        public IReadOnlyList<ContractExport> ExportedContracts =>
            exportedContracts ?? [];
        public IReadOnlyList<ContractRequirement> RequiredContracts =>
            requiredContracts ?? [];
        public IReadOnlyList<ForeignRegistrationProtocolContractExport> ExportedProtocolContracts =>
            exportedProtocolContracts ?? [];
        public IReadOnlyList<ForeignRegistrationProtocolContractRequirement> RequiredProtocolContracts =>
            requiredProtocolContracts ?? [];

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<RegistrationToolDefinition> GetToolDefinitions() => [];

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

        public IForeignRegistrationProtocolContractInvoker GetProtocolContractInvoker(
            string contractName) =>
            new TestProtocolInvoker(contractName);
    }

    private sealed class TestProtocolInvoker(string contractName)
        : IForeignRegistrationProtocolContractInvoker
    {
        public string ContractName => contractName;
        public IReadOnlyList<ForeignRegistrationProtocolContractOperation> Operations => [];

        public Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
