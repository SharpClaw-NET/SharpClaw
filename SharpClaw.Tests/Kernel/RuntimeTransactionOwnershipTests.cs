using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class RuntimeTransactionOwnershipTests
{
    [TestCase(false, false), TestCase(false, true)]
    [TestCase(true, false), TestCase(true, true)]
    public async Task InterruptedBeginJoinsPhysicalAcquisitionAndUntransferredRetirementAsync(bool deadline, bool lateFault)
    {
        var scope = new Scope(deadline, lateFault);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var beginning = scope.Runner.BeginSerializableAsync(cancellation.Token);
        Exception? failure;
        try
        {
            await scope.Begin.WaitForEntryAsync().ConfigureAwait(false);
            if (!deadline) await cancellation.CancelAsync().ConfigureAwait(false);
            await scope.Boundary.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            beginning.IsCompleted.Should().BeFalse();
            scope.Transaction.Disposed.Should().BeFalse();
            scope.AssertHandleHeld();
            scope.Begin.Release();
            if (!lateFault)
            {
                await scope.Retire.WaitForEntryAsync().ConfigureAwait(false);
                beginning.IsCompleted.Should().BeFalse();
                scope.AssertHandleHeld();
            }
        }
        finally
        {
            scope.Begin.Release(); scope.Retire.Release();
            failure = await TestTaskOutcome.CaptureAsync(beginning).ConfigureAwait(false);
        }
        failure.Should().BeSameAs(scope.Boundary.ReceiptFailure);
        failure.Should().BeOfType<KernelActionFailedException>().Which.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
        scope.Transaction.Disposed.Should().BeTrue();
        scope.Begin.Calls.Should().Be(1); scope.Retire.Calls.Should().Be(1);
        using var available = File.Open(scope.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Test]
    public async Task GrantedPassThroughSnapshotsOnlySerializableReceiptAndTransfersActualHandleAsync()
    {
        var scope = new Scope(deadline: false, lateFault: false);
        await using var scopeDisposal = scope.ConfigureAwait(false);
        scope.Begin.Release(); scope.Retire.Release();
        var transaction = await scope.Runner.BeginSerializableAsync(CancellationToken.None).ConfigureAwait(false);
        transaction.Should().BeSameAs(scope.Transaction);
        scope.Transaction.Disposed.Should().BeFalse();
        scope.AssertHandleHeld();
        await scope.Transaction.DisposeAsync().ConfigureAwait(false);
        scope.Transaction.Disposed.Should().BeTrue();
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "transaction-owner-" + Guid.NewGuid().ToString("N"));
        private readonly SharpClawDbContext _db;
        private ResourceTransaction? _transaction;
        internal string Path => System.IO.Path.Combine(_root, "transaction");
        internal ResourceTransaction Transaction => _transaction ?? throw new InvalidOperationException("No actual acquisition.");
        internal ControlledOperationGate Begin { get; }
        internal ControlledOperationGate Retire { get; } = new();
        internal ObservedBoundary Boundary { get; }
        internal RuntimeTransactionActionRunner Runner { get; }

        internal Scope(bool deadline, bool lateFault)
        {
            Directory.CreateDirectory(_root);
            Begin = new(lateFault ? new IOException("late provider acquisition failure") : null);
            var probe = new TransactionProbe(deadline);
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, _root, _root, _root);
            var action = new SharpClawActionKey("storage.transaction.begin");
            var options = ApprovedOptions(probe, action);
            var adapter = RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [probe], paths, new NoProviders(), options);
            Boundary = new((IRuntimeTransactionActionBoundary)adapter);
            _db = new SharpClawDbContext(new DbContextOptionsBuilder<SharpClawDbContext>()
                .UseInMemoryDatabase("transaction-owner-" + Guid.NewGuid().ToString("N")).Options,
                new RuntimePersistenceActionRunner(new UnusedPersistence()));
            Runner = new(_db, Boundary, AcquireAsync);
        }

        private static KernelGraphCompileOptions ApprovedOptions(TransactionProbe probe, SharpClawActionKey action)
        {
            var descriptor = KernelActionCatalog.DescriptorFor(action).ToDescriptor();
            var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
            return new KernelGraphCompileOptions
            {
                ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
                { [probe.Identity.Id] = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal)
                    { [action.Value] = ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap } },
                SensitiveActionApprovals = [new KernelSensitiveActionApproval(probe.Identity.Id, action, descriptor.Version,
                    types.ActionType.AssemblyQualifiedName ?? throw new AssertionException("Missing action type identity."),
                    types.ResultType.AssemblyQualifiedName ?? throw new AssertionException("Missing result type identity."),
                    KernelSchemaIdentity.Action(descriptor))],
            };
        }

        private async Task<IDbContextTransaction> AcquireAsync(CancellationToken cancellationToken)
        {
            _transaction = new ResourceTransaction(Path, Retire);
            try { await Begin.RunAsync().ConfigureAwait(false); return _transaction; }
            catch { Retire.Release(); await _transaction.DisposeAsync().ConfigureAwait(false); throw; }
        }

        internal void AssertHandleHeld()
        {
            Action open = () => { using var handle = File.Open(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
            open.Should().Throw<IOException>();
            _db.Database.ProviderName.Should().NotBeNull();
        }

        public async ValueTask DisposeAsync()
        {
            Begin.Release(); Retire.Release();
            if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false);
            await _db.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ObservedBoundary(IRuntimeTransactionActionBoundary actual) : IRuntimeTransactionActionBoundary
    {
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? ReceiptFailure { get; private set; }
        public async ValueTask<RuntimeTransactionActionResult> RunTransactionActionAsync(RuntimeTransactionActionInvocation invocation,
            Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal, CancellationToken cancellationToken = default)
        {
            try { return await actual.RunTransactionActionAsync(invocation, terminal, cancellationToken).ConfigureAwait(false); }
#pragma warning disable CA1031 // Observe and rethrow the exact real Core receipt for primary-identity assertions; no fixture cleanup runs before the runner joins.
            catch (Exception failure) { ReceiptFailure = failure; Failed.TrySetResult(); throw; }
#pragma warning restore CA1031
        }
    }

    private sealed class TransactionProbe(bool deadline) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("transaction-owner", "Transaction owner", "test");
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<PassThrough>();
            services.OnAction(new SharpClawActionKey("storage.transaction.begin")).Use<PassThrough>(
                new HookOrdering("transaction-owner", Timeout: deadline ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class PassThrough : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken) => control.ProceedAsync(cancellationToken);
    }

    private sealed class UnusedPersistence : IRuntimePersistenceActionBoundary
    {
        public ValueTask RunPersistenceActionAsync(RuntimePersistenceActionInvocation invocation,
            Func<CancellationToken, ValueTask<int>> terminal, CancellationToken cancellationToken = default) =>
            throw new AssertionException("Transaction controls do not save provider data.");
    }

    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins, string providerKey) =>
            throw new AssertionException("Transaction controls do not create inference clients.");
    }

    private sealed class ResourceTransaction(string path, ControlledOperationGate retirement) : IDbContextTransaction
    {
        private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        private readonly Lock _gate = new();
        private Task? _retiring;
        public Guid TransactionId { get; } = Guid.NewGuid();
        public bool SupportsSavepoints => false;
        internal bool Disposed { get; private set; }
        public void Commit() => throw new NotSupportedException();
        public void Rollback() => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { _file.Dispose(); Disposed = true; }
        public ValueTask DisposeAsync() { lock (_gate) return new ValueTask(_retiring ??= RetireAsync()); }
        private async Task RetireAsync()
        {
            try { await retirement.RunAsync().ConfigureAwait(false); }
            finally { await _file.DisposeAsync().ConfigureAwait(false); Disposed = true; }
        }
    }
}
