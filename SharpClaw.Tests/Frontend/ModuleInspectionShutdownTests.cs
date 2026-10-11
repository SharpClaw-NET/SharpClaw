using System.Net;
using System.Text;
using System.Text.Json;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ModuleInspectionShutdownTests
{
    [TestCase(false, "success"), TestCase(false, "cancel"), TestCase(false, "fault")]
    [TestCase(true, "success"), TestCase(true, "cancel"), TestCase(true, "fault")]
    public async Task CloseJoinsAdmittedNetworkInspectionBeforeDisposingItsDependenciesAsync(bool prepare, string outcome)
    {
        var scope = new Scope(outcome);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var operation = InspectAsync(scope, prepare);
        Task shutdown = Task.CompletedTask;
        Task repeated = Task.CompletedTask;
        Exception?[] results;
        try
        {
            await scope.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            shutdown = scope.Store.DisposeAsync().AsTask();
            repeated = scope.Store.DisposeAsync().AsTask();
            repeated.Should().BeSameAs(shutdown);
            await scope.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            AssertClosedAndPending(scope, operation, shutdown);
        }
        finally
        {
            scope.Handler.Release.TrySetResult();
            results = await TestTaskOutcome.JoinAsync(operation, shutdown, repeated).ConfigureAwait(false);
        }
        AssertOutcomes(scope, results, outcome);
        Directory.EnumerateDirectories(scope.Root, "inspection-*").Should().BeEmpty();
        scope.Handler.Disposals.Should().Be(1);
        (await TestTaskOutcome.CaptureAsync(scope.Store.DisposeAsync().AsTask()).ConfigureAwait(false))
            .Should().BeSameAs(results[1]);
    }

    [TestCase(false), TestCase(true)]
    public async Task CloseRetiresAConstructedCandidateBeforeItsOwnershipCanTransferAsync(bool fault)
    {
        var failure = fault ? new IOException("controlled transfer failure") : null;
        var gate = new ControlledOperationGate(failure);
        string? scratch = null;
        var scope = new Scope("success", (candidate, _) =>
        { scratch = candidate.Root; return gate.RunAsync().AsTask(); });
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var operation = InspectLocalAsync(scope);
        Task shutdown = Task.CompletedTask;
        Exception?[] results;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            Directory.Exists(scratch).Should().BeTrue();
            shutdown = scope.Store.DisposeAsync().AsTask();
            AssertClosedAndPending(scope, operation, shutdown);
        }
        finally
        {
            gate.Release();
            results = await TestTaskOutcome.JoinAsync(operation, shutdown).ConfigureAwait(false);
        }
        if (fault) results.Should().OnlyContain(result => ReferenceEquals(result, failure));
        else
        {
            results[0].Should().BeAssignableTo<OperationCanceledException>();
            results[1].Should().BeNull();
        }
        Directory.Exists(scratch).Should().BeFalse();
        scope.Handler.Disposals.Should().Be(1);
    }

    [Test]
    public async Task CloseCancelsQueuedPreparationAndJoinsThePhysicalSemaphoreHolderAsync()
    {
        var scope = new Scope("success");
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var first = InspectAsync(scope, prepare: true);
        Task queued = Task.CompletedTask;
        Task shutdown = Task.CompletedTask;
        try
        {
            await scope.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            queued = InspectLocalAsync(scope);
            shutdown = scope.Store.DisposeAsync().AsTask();
            (await TestTaskOutcome.CaptureAsync(queued).ConfigureAwait(false)).Should().BeAssignableTo<OperationCanceledException>();
            AssertClosedAndPending(scope, first, shutdown);
        }
        finally
        {
            scope.Handler.Release.TrySetResult();
            var results = await TestTaskOutcome.JoinAsync(first, queued, shutdown).ConfigureAwait(false);
            results[0].Should().BeAssignableTo<OperationCanceledException>();
            results[2].Should().BeNull();
        }
        Directory.EnumerateDirectories(scope.Root, "inspection-*").Should().BeEmpty();
        scope.Handler.Disposals.Should().Be(1);
    }

    [Test]
    public async Task StartupCloseClosesInspectionAdmissionBeforeTheBackendDrainSettlesAsync()
    {
        var scope = new Scope("success");
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var mutations = new BundledBackendMutationCoordinator();
        await using var mutationDisposal = mutations.ConfigureAwait(false);
        var startup = new ClientStartupLifetime();
        startup.SetMutationOwner(mutations);
        _ = startup.CreateOwned(() => scope.Store);
        var gate = new ControlledOperationGate();
        var actions = new ClientActionDispatcher();
        var mutation = mutations.RunAsync(actions, "client.module.enablement",
            async _ => await gate.RunAsync().ConfigureAwait(false), CancellationToken.None);
        Task shutdown = Task.CompletedTask;
        Exception?[] results;
        try
        {
            await gate.WaitForEntryAsync().ConfigureAwait(false);
            shutdown = startup.CloseAsync(actions, () => scope.Store.DisposeAsync().AsTask());
            startup.IsClosed.Should().BeTrue();
            shutdown.IsCompleted.Should().BeFalse();
            Action inspect = () => _ = scope.Store.ResolveAsync(scope.Payload, null, CancellationToken.None);
            inspect.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            gate.Release();
            results = await TestTaskOutcome.JoinAsync(mutation, shutdown).ConfigureAwait(false);
        }
        results[1].Should().BeNull();
        gate.Calls.Should().Be(1);
        scope.Handler.Disposals.Should().Be(1);
    }

    [Test]
    public async Task ATransferredCandidateRemainsOwnedByItsCallerAfterStoreCloseAsync()
    {
        var scope = new Scope("success");
        await using var scopeDisposal = scope.ConfigureAwait(false);
        using var candidate = await scope.Store.PrepareAsync(new("local", LocalPath: scope.Payload), null,
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        await scope.Store.DisposeAsync().ConfigureAwait(false);
        Directory.Exists(candidate.Root).Should().BeTrue();
        candidate.Modules.Should().ContainSingle().Which.Id.Should().Be("inspection_fixture");
    }

    private static async Task InspectAsync(Scope scope, bool prepare)
    {
        if (prepare)
        {
            using var candidate = await scope.Store.PrepareAsync(new("fixture.zip", Download: new Uri(
                "https://github.com/owner/repo/releases/download/v1/fixture.zip")), null, CancellationToken.None).ConfigureAwait(false);
        }
        else _ = await scope.Store.ResolveAsync("https://github.com/owner/repo/releases/tag/v1", null, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task InspectLocalAsync(Scope scope)
    {
        using var candidate = await scope.Store.PrepareAsync(new("local", LocalPath: scope.Payload), null, CancellationToken.None).ConfigureAwait(false);
    }

    private static void AssertClosedAndPending(Scope scope, Task operation, Task shutdown)
    {
        operation.IsCompleted.Should().BeFalse();
        shutdown.IsCompleted.Should().BeFalse();
        scope.Handler.Disposals.Should().Be(0);
        Action resolve = () => _ = scope.Store.ResolveAsync(scope.Payload, null, CancellationToken.None);
        resolve.Should().Throw<ObjectDisposedException>();
        Action prepare = () => _ = scope.Store.PrepareAsync(new("local", LocalPath: scope.Payload), null, CancellationToken.None);
        prepare.Should().Throw<ObjectDisposedException>();
    }

    private static void AssertOutcomes(Scope scope, Exception?[] results, string outcome)
    {
        if (string.Equals(outcome, "fault", StringComparison.Ordinal))
            results.Should().OnlyContain(result => ReferenceEquals(result, scope.Handler.Failure));
        else
        {
            results[0].Should().BeAssignableTo<OperationCanceledException>();
            results[1].Should().BeNull();
            results[2].Should().BeNull();
        }
    }

    private sealed class InspectionHandler(string outcome) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IOException Failure { get; } = new("controlled inspection transport failure");
        public int Disposals { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            if (string.Equals(outcome, "fault", StringComparison.Ordinal)) throw Failure;
            if (string.Equals(outcome, "cancel", StringComparison.Ordinal)) throw new OperationCanceledException(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"assets\":[{\"name\":\"fixture.zip\",\"browser_download_url\":\"https://github.com/owner/repo/releases/download/v1/fixture.zip\"}]}", Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class Scope : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharpclaw-inspection-close-" + Guid.NewGuid().ToString("N"));
        public string Payload => Path.Combine(Root, "fixture-payload");
        public InspectionHandler Handler { get; }
        public ModulePackageStore Store { get; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
            Justification = "Store owns sources, HTTP and handler; its constructor disposes acquired sources on failure, and the fixture always joins store shutdown.")]
        public Scope(string outcome, Func<PreparedModulePackage, CancellationToken, Task>? beforeTransfer = null)
        {
            Directory.CreateDirectory(Payload);
            File.Copy(typeof(ModuleInspectionShutdownTests).Assembly.Location, Path.Combine(Payload, "Fixture.dll"));
            File.WriteAllText(Path.Combine(Payload, "package.json"), JsonSerializer.Serialize(new
            { id = "inspection_fixture", displayName = "Inspection fixture", version = "0.5.0", toolPrefix = "if",
                entryAssembly = "Fixture.dll", minHostVersion = "0.1.0", runtime = "dotnet", hostMode = "in-process" }));
            Handler = new InspectionHandler(outcome);
            Store = new ModulePackageStore(Root, new ModulePackageSources(new HttpClient(Handler, disposeHandler: true)), beforeTransfer);
        }

        public async ValueTask DisposeAsync()
        {
            Handler.Release.TrySetResult();
            _ = await TestTaskOutcome.CaptureAsync(Store.DisposeAsync().AsTask()).ConfigureAwait(false);
            Directory.Delete(Root, recursive: true);
        }
    }
}
