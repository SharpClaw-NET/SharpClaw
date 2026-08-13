using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Modules;
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
            "Runtime persistence writes must enter through SaveChangesThroughKernelAsync");
    }

    [Test]
    public async Task Persistence_action_must_run_its_terminal()
    {
        var setup = CreateDatabase(new TestPersistenceBoundary(runTerminal: false));
        await using var db = setup.Db;
        var boundary = setup.Runner;

        Func<Task> action = async () => await boundary.SaveChangesAsync(db);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Persistence action completed without running its save terminal.");
    }

    [Test]
    public async Task Repeated_persistence_action_runs_one_save_terminal()
    {
        var actionBoundary = new TestPersistenceBoundary(runTerminal: true, repeatTerminal: true);
        var setup = CreateDatabase(actionBoundary);
        await using var db = setup.Db;
        var runner = setup.Runner;
        db.Models.Add(new SharpClaw.Contracts.Entities.Core.ModelDB
        {
            Name = "one",
            ProviderId = Guid.NewGuid(),
        });

        var saved = await runner.SaveChangesAsync(db);

        saved.Should().Be(1);
        actionBoundary.TerminalCalls.Should().Be(2);
        (await db.Models.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Persistence_terminal_failure_is_not_repeated_or_hidden()
    {
        var actionBoundary = new TestPersistenceBoundary(
            runTerminal: true,
            repeatTerminal: true,
            terminalFailure: new InvalidOperationException("persistence failed"));
        var setup = CreateDatabase(actionBoundary);
        await using var db = setup.Db;
        var runner = setup.Runner;

        Func<Task> action = async () => await runner.SaveChangesAsync(db);

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
        var setup = CreateDatabase(actionBoundary);
        await using var db = setup.Db;
        var runner = setup.Runner;

        Func<Task> action = async () => await runner.SaveChangesAsync(db);

        await action.Should().ThrowAsync<OperationCanceledException>();
        actionBoundary.TerminalCalls.Should().Be(0);
    }

    private static bool IsOwnedSaveBoundary(string path, string line)
    {
        var fileName = Path.GetFileName(path);
        return fileName switch
        {
            "SharpClawDbContext.cs" => line.Contains(
                "base.SaveChangesAsync(",
                StringComparison.Ordinal)
                || line.Contains(
                    "runner.SaveChangesAsync(",
                    StringComparison.Ordinal)
                || line.Contains(
                    "GetRequiredRunner().SaveChangesAsync(",
                    StringComparison.Ordinal)
                || line.Contains(
                    "public override async Task<int> SaveChangesAsync(",
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

    private static (SharpClawDbContext Db, RuntimePersistenceActionRunner Runner) CreateDatabase(
        TestPersistenceBoundary boundary)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase("persistence-boundary-" + Guid.NewGuid().ToString("N"))
            .Options;
        var runner = new RuntimePersistenceActionRunner(boundary);
        var db = new SharpClawDbContext(options, new TestPersistenceActionRunnerAccessor(runner));
        db.Database.EnsureCreated();
        return (db, runner);
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
        public int TerminalCalls { get; private set; }
        public Exception? Cancellation { get; init; }

        public async ValueTask RunPersistenceActionAsync(
            RuntimePersistenceActionInvocation invocation,
            Func<CancellationToken, ValueTask<int>> terminal,
            CancellationToken cancellationToken = default)
        {
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

            _ = await terminal(cancellationToken);
        }
    }

    private sealed class TestPersistenceActionRunnerAccessor(
        RuntimePersistenceActionRunner runner) : IRuntimePersistenceActionRunnerAccessor
    {
        public RuntimePersistenceActionRunner GetRequiredRunner() => runner;
    }
}
