using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class RuntimeStartupInterruptionTests
{
    [TestCase(false, false, false), TestCase(false, false, true)]
    [TestCase(false, true, false), TestCase(false, true, true)]
    [TestCase(true, false, false), TestCase(true, false, true)]
    [TestCase(true, true, false), TestCase(true, true, true)]
    public async Task InterruptedStartupJoinsPhysicalWorkAndCompensationBeforeRootRetirementAsync(
        bool deadline, bool lateFault, bool cleanupFault)
    {
        var workspace = new Workspace(deadline, lateFault, cleanupFault);
        await using var workspaceDisposal = workspace.ConfigureAwait(false);
        using var caller = new CancellationTokenSource();
        var running = workspace.RunHostAsync(caller.Token);
        Task stopping = Task.CompletedTask;
        Task repeated = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await workspace.Probe.StartGate.WaitForEntryAsync().ConfigureAwait(false);
            if (!deadline) await caller.CancelAsync().ConfigureAwait(false);
            await workspace.Probe.ReceiptObserved.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            stopping = workspace.StopAsync();
            repeated = workspace.StopAsync();
            AssertPending(workspace, running, stopping, repeated);
            workspace.Probe.StartGate.Release();
            await workspace.Early.StopGate.WaitForEntryAsync().ConfigureAwait(false);
            AssertPending(workspace, running, stopping, repeated);
            workspace.Probe.Stops.Should().Be(lateFault ? 0 : 1);
        }
        finally
        {
            workspace.Probe.StartGate.Release();
            workspace.Early.StopGate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(running, stopping, repeated).ConfigureAwait(false);
        }
        var failure = outcomes[0].Should().BeOfType<KernelActionFailedException>().Which;
        failure.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
        outcomes[1].Should().BeSameAs(failure);
        outcomes[2].Should().BeSameAs(failure);
        (await TestTaskOutcome.CaptureAsync(workspace.StopAsync()).ConfigureAwait(false)).Should().BeSameAs(failure);
        workspace.Probe.StartGate.Calls.Should().Be(1);
        workspace.Early.StopGate.Calls.Should().Be(1);
        workspace.CompletionCalls.Should().Be(1);
        workspace.CredentialCleanupCalls.Should().Be(1);
        workspace.Root.Disposed.Should().BeTrue();
        workspace.AssertFilesAvailable();
    }

    private static void AssertPending(Workspace workspace, params Task[] tasks)
    {
        tasks.Should().OnlyContain(task => !task.IsCompleted);
        workspace.CompletionCalls.Should().Be(0);
        workspace.CredentialCleanupCalls.Should().Be(0);
        workspace.Root.Disposed.Should().BeFalse();
        workspace.AssertRootFileHeld();
    }

    private sealed class StartupProbe(bool deadline, bool fail, string path) : ISharpClawModule, IAsyncDisposable
    {
        private FileStream? _resource;
        private Task? _starting;
        public RootDependency? Dependency { private get; set; }
        public ModuleIdentity Identity { get; } = new("startup-interruption", "Startup interruption", "test");
        public ControlledOperationGate StartGate { get; } = new(fail ? new IOException("late physical startup fault") : null);
        public TaskCompletionSource ReceiptObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(this);
            services.AddSingleton<StartupInterceptor>();
            services.OnAction(new SharpClawActionKey("runtime.start.configure")).Use<StartupInterceptor>(new HookOrdering(
                "startup-interruption", Timeout: deadline ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5)));
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken) =>
            new(_starting ??= StartCoreAsync());

        private async Task StartCoreAsync()
        {
            var dependency = Dependency ?? throw new InvalidOperationException("Root dependency was not attached.");
            dependency.AssertAvailable();
            _resource = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                try { await StartGate.RunAsync().ConfigureAwait(false); }
                finally { dependency.AssertAvailable(); }
            }
            catch { await RetireResourceAsync().ConfigureAwait(false); throw; }
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            await DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_starting is { } starting)
                await TestTaskOutcome.CaptureAsync(starting).ConfigureAwait(false);
            if (_resource is not null) await _resource.DisposeAsync().ConfigureAwait(false);
            _resource = null;
        }

        private async ValueTask RetireResourceAsync()
        {
            if (_resource is not null) await _resource.DisposeAsync().ConfigureAwait(false);
            _resource = null;
        }
    }

    private sealed class EarlyParticipant(string path, bool fail) : ISharpClawModule, IAsyncDisposable
    {
        private FileStream? _resource;
        private Task? _stopping;
        public RootDependency? Dependency { private get; set; }
        public ModuleIdentity Identity { get; } = new("startup-early", "Early participant", "test");
        public ControlledOperationGate StopGate { get; } = new(fail ? new IOException("physical compensation fault") : null);
        public void ConfigureServices(IServiceCollection services) { }
        public ValueTask StartAsync(ServiceStartContext context, CancellationToken cancellationToken)
        {
            _resource = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken) => new(_stopping ??= StopCoreAsync());

        private async Task StopCoreAsync()
        {
            var dependency = Dependency ?? throw new InvalidOperationException("Root dependency was not attached.");
            try
            {
                try { await StopGate.RunAsync().ConfigureAwait(false); }
                finally { dependency.AssertAvailable(); }
            }
            finally { await RetireResourceAsync().ConfigureAwait(false); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopping is { } stopping)
                await TestTaskOutcome.CaptureAsync(stopping).ConfigureAwait(false);
            if (_resource is not null) await _resource.DisposeAsync().ConfigureAwait(false);
            _resource = null;
        }

        private async ValueTask RetireResourceAsync()
        {
            if (_resource is not null) await _resource.DisposeAsync().ConfigureAwait(false);
            _resource = null;
        }
    }

    private sealed class StartupInterceptor(StartupProbe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            try { return await control.ProceedAsync(cancellationToken).ConfigureAwait(false); }
            finally { probe.ReceiptObserved.TrySetResult(); }
        }
    }

    private sealed class RootDependency(string path) : IDisposable
    {
        private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        public bool Disposed { get; private set; }
        public void AssertAvailable() => _file.WriteByte(1);
        public void Dispose() { _file.Dispose(); Disposed = true; }
    }

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sharpclaw-startup-" + Guid.NewGuid().ToString("N"));
        private readonly IHost _host;
        private readonly RuntimeKernelAdapter _adapter;
        private readonly RuntimeHostCleanup _cleanup;
        public StartupProbe Probe { get; }
        public EarlyParticipant Early { get; }
        public RootDependency Root { get; }
        public int CompletionCalls { get; private set; }
        public int CredentialCleanupCalls { get; private set; }

        public Workspace(bool deadline, bool lateFault, bool cleanupFault)
        {
            Directory.CreateDirectory(_directory);
            Early = new(Path.Combine(_directory, "early"), cleanupFault);
            Probe = new(deadline, lateFault, Path.Combine(_directory, "late"));
            var services = TestServiceGraph.Collect([Early, Probe]);
            services.AddSingleton(_ => new RootDependency(Path.Combine(_directory, "root")));
            _host = new Microsoft.Extensions.Hosting.HostBuilder().ConfigureServices(collection =>
            {
                foreach (var descriptor in services) collection.Add(descriptor);
            }).Build();
            try
            {
                Root = _host.Services.GetRequiredService<RootDependency>();
                Probe.Dependency = Root; Early.Dependency = Root;
                var action = new SharpClawActionKey("runtime.start.configure");
                var options = new KernelGraphCompileOptions
                {
                    ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
                    {
                        [Probe.Identity.Id] = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal)
                        { [action.Value] = KernelActionCatalog.DescriptorFor(action).Capabilities },
                    },
                };
                var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, _directory, _directory, _directory);
                _adapter = new(new ConfigurationBuilder().Build(), _host.Services, new KernelJobsBindings(),
                    [Early, Probe], paths, new NoProviders(), options);
                _cleanup = new RuntimeHostCleanup(static () => { }, () =>
                {
                    CompletionCalls++;
                    if (cleanupFault) throw new IOException("discovery cleanup fault");
                }, () => CredentialCleanupCalls++, static () => ValueTask.CompletedTask);
            }
            catch { _host.Dispose(); Directory.Delete(_directory, recursive: true); throw; }
        }

        public async Task RunHostAsync(CancellationToken cancellationToken)
        {
            ExceptionDispatchInfo? failure = null;
            try { await _adapter.StartAsync("startup-interruption", cancellationToken: cancellationToken).ConfigureAwait(false); }
#pragma warning disable CA1031 // Test transports the actual startup error through the production cleanup path before rethrowing it.
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
#pragma warning restore CA1031
            finally
            {
                failure = await LocalRuntimeHost.StopAndCleanUpAsync(_adapter, _cleanup, failure).ConfigureAwait(false);
                if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
                else _host.Dispose();
            }
            failure?.Throw();
        }

        public Task StopAsync() => _adapter.StopAsync(CancellationToken.None,
            _ => _cleanup.BeginAsync(), _ => _cleanup.CompleteAsync()).AsTask();

        public void AssertRootFileHeld()
        {
            Action open = () => { using var stream = File.Open(Path.Combine(_directory, "root"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
            open.Should().Throw<IOException>();
        }

        public void AssertFilesAvailable()
        {
            foreach (var file in new[] { "root", "early", "late" })
            {
                using var stream = File.Open(Path.Combine(_directory, file), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stream.CanRead.Should().BeTrue();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Probe.StartGate.Release(); Early.StopGate.Release();
            await Probe.DisposeAsync().ConfigureAwait(false);
            await Early.DisposeAsync().ConfigureAwait(false);
            if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
            else _host.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => throw new AssertionException("Startup controls must not construct a provider client.");
    }
}
