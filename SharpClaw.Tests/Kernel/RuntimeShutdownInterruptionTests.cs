using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class RuntimeShutdownInterruptionTests
{
    [TestCase(false, false, false), TestCase(false, false, true)]
    [TestCase(false, true, false), TestCase(false, true, true)]
    [TestCase(true, true, false), TestCase(true, true, true)]
    public async Task InterruptedReceiptCannotRetireAnUnsettledShutdownPhaseAsync(bool completion, bool deadline, bool fail)
    {
        using var workspace = new Workspace(completion, deadline);
        using var caller = new CancellationTokenSource();
        var physicalFailure = fail ? new IOException("controlled physical shutdown failure") : null;
        var callbacks = new ShutdownCallbacks(completion, physicalFailure);
        await workspace.Adapter.StartAsync("shutdown-interruption",
            cancellationToken: TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var first = workspace.Adapter.StopAsync(caller.Token, callbacks.PrepareAsync, callbacks.CompleteAsync).AsTask();
        Task second = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await callbacks.Gate.WaitForEntryAsync().ConfigureAwait(false);
            if (!deadline) await caller.CancelAsync().ConfigureAwait(false);
            // This signal follows the REAL Core continuation's interrupted receipt,
            // including its cancellation observation window. Physical work stays held.
            await workspace.Probe.ContinuationSettled.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            second = workspace.Adapter.StopAsync(CancellationToken.None).AsTask();
            AssertPending(first, second, callbacks, completion);
        }
        finally
        {
            callbacks.Gate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(first, second).ConfigureAwait(false);
        }
        var receiptFailure = outcomes[0].Should().BeOfType<KernelActionFailedException>().Which;
        receiptFailure.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
        outcomes[1].Should().BeSameAs(receiptFailure);
        (await TestTaskOutcome.CaptureAsync(workspace.Adapter.StopAsync(CancellationToken.None).AsTask()).ConfigureAwait(false))
            .Should().BeSameAs(receiptFailure);
        outcomes[0].Should().NotBeSameAs(physicalFailure);
        callbacks.Gate.Calls.Should().Be(1);
        callbacks.DiscoveryDeleted.Should().Be(1);
        callbacks.CredentialsDeleted.Should().Be(1);
        workspace.Probe.ParticipantStops.Should().Be(1);
    }

    private static void AssertPending(Task first, Task second, ShutdownCallbacks callbacks, bool completion)
    {
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        callbacks.CompletionEntries.Should().Be(completion ? 1 : 0);
        callbacks.DiscoveryDeleted.Should().Be(0);
        callbacks.CredentialsDeleted.Should().Be(0);
    }

    private sealed class ShutdownCallbacks
    {
        private readonly bool _completion;
        private readonly RuntimeHostCleanup _cleanup;
        public ControlledOperationGate Gate { get; }
        public int CompletionEntries { get; private set; }
        public int DiscoveryDeleted { get; private set; }
        public int CredentialsDeleted { get; private set; }

        public ShutdownCallbacks(bool completion, Exception? failure)
        {
            _completion = completion;
            Gate = new ControlledOperationGate(failure);
            _cleanup = new RuntimeHostCleanup(static () => { }, () => DiscoveryDeleted++,
                () => CredentialsDeleted++, completion ? static () => ValueTask.CompletedTask : Gate.RunAsync);
        }

        public ValueTask PrepareAsync(CancellationToken _) => _cleanup.BeginAsync();

        public async ValueTask CompleteAsync(CancellationToken _)
        {
            CompletionEntries++;
            try { if (_completion) await Gate.RunAsync().ConfigureAwait(false); }
            finally { await _cleanup.CompleteAsync().ConfigureAwait(false); }
        }
    }

    private sealed class InterruptionProbe(string action, bool deadline) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("shutdown-interruption", "Shutdown interruption", "test");
        public TaskCompletionSource ContinuationSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ParticipantStops { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(this);
            services.AddSingleton<InterruptionInterceptor>();
            services.OnAction(new SharpClawActionKey(action)).Use<InterruptionInterceptor>(new HookOrdering(
                "shutdown-interruption", Timeout: deadline ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5)));
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) { ParticipantStops++; return ValueTask.CompletedTask; }
    }

    private sealed class InterruptionInterceptor(InterruptionProbe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            try { return await control.ProceedAsync(cancellationToken).ConfigureAwait(false); }
            finally { probe.ContinuationSettled.TrySetResult(); }
        }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-interrupted-shutdown-" + Guid.NewGuid().ToString("N"));
        public InterruptionProbe Probe { get; }
        public RuntimeKernelAdapter Adapter { get; }

        public Workspace(bool completion, bool deadline)
        {
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, _root, _root, _root);
            paths.EnsureDirectories();
            var action = new SharpClawActionKey(completion ? "runtime.stop.complete" : "runtime.stop.prepare");
            Probe = new InterruptionProbe(action.Value, deadline);
            var options = new KernelGraphCompileOptions
            {
                ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
                {
                    ["shutdown-interruption"] = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal)
                    { [action.Value] = KernelActionCatalog.DescriptorFor(action).Capabilities },
                },
            };
            try { Adapter = RuntimeKernelAdapterTestFactory.Create(new ConfigurationBuilder().Build(), [Probe], paths, new NoProviders(), options); }
            catch { Directory.Delete(_root, recursive: true); throw; }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => throw new AssertionException("Shutdown must not construct a provider client.");
    }
}
