using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimePersistenceBoundaryTests
{
    [Test]
    public void Persistence_manifest_matches_the_published_non_transaction_catalog()
    {
        var expected = SharpClawActionCatalog.Kernel
            .Where(static key =>
                key.Value.StartsWith("storage.", StringComparison.Ordinal)
                && !key.Value.StartsWith(
                    "storage.transaction.",
                    StringComparison.Ordinal))
            .Select(static key => key.Value)
            .ToArray();

        RuntimePersistenceActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(expected);
    }

    [Test]
    public void SharpClaw_model_contains_only_the_approved_base_entities()
    {
        using var db = CreateDatabase(new TestPersistenceBoundary(runTerminal: true));

        db.Model.GetEntityTypes()
            .Select(static entity => entity.ClrType)
            .Should()
            .BeEquivalentTo(
                [
                    typeof(ProviderDB),
                    typeof(ModelDB),
                    typeof(RegistrationStateDB),
                    typeof(ConfigurationEntryDB),
                    typeof(ScopedStorageRecordDB),
                    typeof(ScopedStorageIndexEntryDB),
                ]);
    }

    [Test]
    public void Runtime_save_calls_cannot_bypass_the_kernel_boundary()
    {
        var sourceRoot = FindSourceRoot();
        var runtimeSources = Directory.EnumerateFiles(
                Path.Combine(sourceRoot, "SharpClaw.Runtime"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));

        var offenders = runtimeSources
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(entry => entry.Text.Contains("SaveChangesAsync(", StringComparison.Ordinal))
            .Where(entry => !IsOwnedSaveBoundary(entry.Path, entry.Text))
            .Select(entry => $"{entry.Path}:{entry.Line}")
            .ToArray();

        offenders.Should().BeEmpty(
            "Runtime persistence writes must enter through the coordinated SharpClawDbContext SaveChangesAsync overrides");

        var terminalCallers = runtimeSources
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(entry => entry.Text.Contains(
                "SaveChangesTerminalAsync(",
                StringComparison.Ordinal))
            .Where(entry => Path.GetFileName(entry.Path) != "RuntimePersistenceActionBoundary.cs")
            .Select(entry => $"{entry.Path}:{entry.Line}")
            .ToArray();

        terminalCallers.Should().BeEmpty(
            "only the Runtime persistence action runner may invoke the package-internal save terminal");
    }

    [Test]
    public void PackagedContextExposesNoPublicSaveTerminal()
    {
        typeof(SharpClawDbContext).IsSealed.Should().BeTrue();
        var declaredPublicMethods = typeof(SharpClawDbContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .ToArray();

        declaredPublicMethods.Should().NotContain(method =>
                method.Name.Contains("Terminal", StringComparison.Ordinal)
                || method.Name.Contains("ThroughKernel", StringComparison.Ordinal));
        declaredPublicMethods.Where(method => method.Name == nameof(DbContext.SaveChanges))
            .Should().HaveCount(2);
        declaredPublicMethods.Where(method => method.Name == nameof(DbContext.SaveChangesAsync))
            .Should().HaveCount(2);

        var terminal = typeof(SharpClawDbContext)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Should().ContainSingle(method => method.Name == "SaveChangesTerminalAsync")
            .Which;
        terminal.IsAssembly.Should().BeTrue();
        typeof(SharpClawDbContext).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Should().Equal("SharpClaw.Runtime.INF");

        var coordinatorMethod = typeof(SharpClaw.Persistence.ISharpClawPersistenceSaveCoordinator)
            .GetMethod(nameof(SharpClaw.Persistence.ISharpClawPersistenceSaveCoordinator.SaveChangesAsync));
        coordinatorMethod.Should().NotBeNull();
        coordinatorMethod!.GetParameters()
            .Should().NotContain(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    [Test]
    public async Task Persistence_action_must_run_its_terminal()
    {
        await using var db = CreateDatabase(new TestPersistenceBoundary(runTerminal: false));

        Func<Task> action = async () => await db.SaveChangesAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Persistence action completed without running its save terminal.");
    }

    [Test]
    public async Task Async_save_enters_one_action_and_runs_one_terminal_despite_repeated_requests()
    {
        var actionBoundary = new TestPersistenceBoundary(runTerminal: true, repeatTerminal: true);
        await using var db = CreateDatabase(actionBoundary);
        db.Models.Add(new SharpClaw.Contracts.Entities.Core.ModelDB
        {
            Name = "one",
            ProviderId = Guid.NewGuid(),
        });

        var saved = await db.SaveChangesAsync(acceptAllChangesOnSuccess: true);

        saved.Should().Be(1);
        actionBoundary.ActionCalls.Should().Be(1);
        actionBoundary.TerminalCalls.Should().Be(2);
        actionBoundary.TerminalResults.Should().Equal(1, 1);
        (await db.Models.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Persistence_terminal_failure_is_not_repeated_or_hidden()
    {
        var actionBoundary = new TestPersistenceBoundary(
            runTerminal: true,
            repeatTerminal: true,
            terminalFailure: new InvalidOperationException("persistence failed"));
        await using var db = CreateDatabase(actionBoundary);

        Func<Task> action = async () => await db.SaveChangesAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("persistence failed");
        actionBoundary.TerminalCalls.Should().Be(1);
    }

    [Test]
    public async Task Persistence_action_cancellation_prevents_the_save_terminal()
    {
        var actionBoundary = new TestPersistenceBoundary(runTerminal: false)
        {
            Cancellation = new OperationCanceledException(),
        };
        await using var db = CreateDatabase(actionBoundary);

        Func<Task> action = async () => await db.SaveChangesAsync();

        await action.Should().ThrowAsync<OperationCanceledException>();
        actionBoundary.TerminalCalls.Should().Be(0);
    }

    private static bool IsOwnedSaveBoundary(string path, string line)
    {
        var fileName = Path.GetFileName(path);
        return fileName switch
        {
            "RuntimeModelRegistrar.cs" or
            "ScopedStorageGateway.cs" or
            "RuntimeScopedStorageEventOutboxStore.cs" => line.Contains(
                "db.SaveChangesAsync(",
                StringComparison.Ordinal),
            "CoreStateSession.cs" => line.Contains(
                "public async Task<int> SaveChangesAsync(",
                StringComparison.Ordinal)
                || line.Contains("_states.SaveChangesAsync(", StringComparison.Ordinal),
            "RuntimePersistenceActionBoundary.cs" => line.Contains(
                "SaveChangesAsync(",
                StringComparison.Ordinal),
            _ => line.Contains("_states.SaveChangesAsync(", StringComparison.Ordinal),
        };
    }

    private static SharpClawDbContext CreateDatabase(
        TestPersistenceBoundary boundary)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase("persistence-boundary-" + Guid.NewGuid().ToString("N"))
            .Options;
        var runner = new RuntimePersistenceActionRunner(boundary);
        var db = new SharpClawDbContext(options, runner);
        db.Database.EnsureCreated();
        return db;
    }

    private static string FindSourceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            Directory.Exists(Path.Combine(configuredRoot, "SharpClaw.Runtime")))
            return configuredRoot;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }

    private sealed class TestPersistenceBoundary(
        bool runTerminal,
        bool repeatTerminal = false,
        Exception? terminalFailure = null) : IRuntimePersistenceActionBoundary
    {
        public int ActionCalls { get; private set; }
        public int TerminalCalls { get; private set; }
        public List<int> TerminalResults { get; } = [];
        public Exception? Cancellation { get; init; }

        public async ValueTask RunPersistenceActionAsync(
            RuntimePersistenceActionInvocation invocation,
            Func<CancellationToken, ValueTask<int>> terminal,
            CancellationToken cancellationToken = default)
        {
            ActionCalls++;
            invocation.ActionKey.Value.Should().Be("storage.upsert.commit");
            if (Cancellation is not null)
                throw Cancellation;
            if (!runTerminal)
                return;

            await InvokeTerminalAsync(terminal, terminalFailure, cancellationToken);
            if (repeatTerminal)
                await InvokeTerminalAsync(terminal, terminalFailure, cancellationToken);
        }

        private async ValueTask InvokeTerminalAsync(
            Func<CancellationToken, ValueTask<int>> terminal,
            Exception? failure,
            CancellationToken cancellationToken)
        {
            TerminalCalls++;
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            TerminalResults.Add(await terminal(cancellationToken));
        }
    }

}
