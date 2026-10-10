using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this internal fixture through reflection.")]
[TestFixture]
internal sealed class RuntimeShutdownOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedCleanupPreparationJoinsTheSameSettledOutcomeAsync(bool fail)
    {
        var failure = fail ? new IOException("controlled listener failure") : null;
        var gate = new ControlledOperationGate(failure);
        var readinessCalls = 0;
        var cleanup = new RuntimeHostCleanup(() => readinessCalls++, static () => { },
            static () => { }, gate.RunAsync);
        var first = cleanup.BeginAsync().AsTask();
        Task second = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            second = cleanup.BeginAsync().AsTask();
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            gate.Calls.Should().Be(1);
            readinessCalls.Should().Be(1);
        }
        finally
        {
            gate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(first, second).ConfigureAwait(false);
        }
        outcomes[0].Should().BeSameAs(failure);
        outcomes[1].Should().BeSameAs(failure);
        (await TestTaskOutcome.CaptureAsync(cleanup.BeginAsync().AsTask()).ConfigureAwait(false))
            .Should().BeSameAs(failure);
        gate.Calls.Should().Be(1);
    }

    [Test]
    public async Task RepeatedCleanupCompletionRetainsFirstFailureAndAttemptsEveryResourceAsync()
    {
        var failure = new IOException("controlled discovery cleanup failure");
        var deleted = 0;
        var keyCleaned = 0;
        var cleanup = new RuntimeHostCleanup(static () => { },
            () => { deleted++; throw failure; }, () => keyCleaned++, static () => ValueTask.CompletedTask);
        var first = cleanup.CompleteAsync().AsTask();
        var second = cleanup.CompleteAsync().AsTask();
        var outcomes = await TestTaskOutcome.JoinAsync(first, second).ConfigureAwait(false);
        outcomes[0].Should().BeSameAs(failure);
        outcomes[1].Should().BeSameAs(failure);
        deleted.Should().Be(1);
        keyCleaned.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedAdapterStopJoinsPreparationAndRunsCompletionOnceAsync(bool fail)
    {
        using var workspace = new Workspace();
        var gate = new ControlledOperationGate(fail ? new IOException("controlled host stop failure") : null);
        var adapter = RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [],
            workspace.Paths, new NoProviders());
        await adapter.StartAsync("shutdown-ownership", cancellationToken: TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
        var completeCalls = 0;
        var first = adapter.StopAsync(CancellationToken.None, _ => gate.RunAsync(),
            _ => { completeCalls++; return ValueTask.CompletedTask; }).AsTask();
        Task second = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            second = adapter.StopAsync(CancellationToken.None).AsTask();
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            completeCalls.Should().Be(0);
        }
        finally
        {
            gate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(first, second).ConfigureAwait(false);
        }
        if (fail)
            outcomes[0].Should().NotBeNull();
        else
            outcomes[0].Should().BeNull();
        outcomes[1].Should().BeSameAs(outcomes[0]);
        (await TestTaskOutcome.CaptureAsync(adapter.StopAsync(CancellationToken.None).AsTask()).ConfigureAwait(false))
            .Should().BeSameAs(outcomes[0]);
        gate.Calls.Should().Be(1);
        completeCalls.Should().Be(1);
    }

    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => throw new AssertionException("Shutdown must not construct a provider client.");
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-shutdown-" + Guid.NewGuid().ToString("N"));
        public SharpClawInstancePaths Paths { get; }
        public Workspace()
        {
            Paths = new(SharpClawInstanceKind.Backend, _root, _root, _root);
            Paths.EnsureDirectories();
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
