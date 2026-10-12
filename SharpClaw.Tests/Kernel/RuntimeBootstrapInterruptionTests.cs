using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Persistence;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this regression fixture.")]
[TestFixture]
internal sealed class RuntimeBootstrapInterruptionTests
{
    [TestCase(false, false, false, false), TestCase(false, false, false, true)]
    [TestCase(false, false, true, false), TestCase(false, false, true, true)]
    [TestCase(false, true, false, false), TestCase(false, true, false, true)]
    [TestCase(false, true, true, false), TestCase(false, true, true, true)]
    [TestCase(true, false, false, false), TestCase(true, false, false, true)]
    [TestCase(true, false, true, false), TestCase(true, false, true, true)]
    [TestCase(true, true, false, false), TestCase(true, true, false, true)]
    [TestCase(true, true, true, false), TestCase(true, true, true, true)]
    public async Task BootstrapReceiptCannotRetireInitializationOrBindingAsync(
        bool initialize, bool deadline, bool lateFault, bool cleanupFault)
    {
        var workspace = new Workspace(initialize, deadline, lateFault, cleanupFault);
        await using var disposal = workspace.ConfigureAwait(false);
        using var caller = new CancellationTokenSource();
        var running = workspace.RunAsync(caller.Token);
        Task stopping = Task.CompletedTask;
        Task repeated = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await workspace.Physical.WaitForEntryAsync().ConfigureAwait(false);
            if (!deadline) await caller.CancelAsync().ConfigureAwait(false);
            await workspace.Probe.ReceiptObserved.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            stopping = workspace.StopAsync();
            repeated = workspace.StopAsync();
            AssertPending(workspace, running, stopping, repeated);
            workspace.Physical.Release();
            await workspace.CleanupGate.WaitForEntryAsync().ConfigureAwait(false);
            AssertPending(workspace, running, stopping, repeated);
        }
        finally
        {
            workspace.Physical.Release(); workspace.CleanupGate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(running, stopping, repeated).ConfigureAwait(false);
        }
        var primary = outcomes[0].Should().BeOfType<KernelActionFailedException>().Which;
        primary.Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
        outcomes[1].Should().BeSameAs(primary);
        outcomes[2].Should().BeSameAs(primary);
        (await TestTaskOutcome.CaptureAsync(workspace.StopAsync()).ConfigureAwait(false)).Should().BeSameAs(primary);
        workspace.Physical.Calls.Should().Be(1);
        workspace.CleanupGate.Calls.Should().Be(1);
        workspace.ListenerStops.Should().Be(initialize ? 0 : 1);
        workspace.CredentialCleanups.Should().Be(1);
        workspace.Root.Disposed.Should().BeTrue();
        workspace.AssertFilesAvailable();
        workspace.Readiness.IsReady.Should().BeFalse();
        File.Exists(workspace.Paths.DiscoveryEntryPath).Should().BeFalse();
    }

    [TestCase(0), TestCase(1), TestCase(2)]
    public async Task BindingPublishesOnlyBeforeKernelOrHostCloseAsync(int closeKind)
    {
        var workspace = new Workspace(initialize: false, deadline: false, lateFault: false, cleanupFault: false);
        await using var disposal = workspace.ConfigureAwait(false);
        var running = workspace.RunAsync(CancellationToken.None);
        Task stopping = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await workspace.Physical.WaitForEntryAsync().ConfigureAwait(false);
            if (closeKind == 1) stopping = workspace.StopAsync();
            if (closeKind == 2) workspace.RequestHostStop();
            workspace.Physical.Release();
            await workspace.BindingReturned.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            await workspace.CleanupGate.WaitForEntryAsync().ConfigureAwait(false);
            workspace.BindingPublished.Should().Be(closeKind == 0);
            workspace.ReadyAtBindingReturn.Should().Be(closeKind == 0);
            workspace.DiscoveryAtBindingReturn.Should().Be(closeKind == 0);
            File.Exists(workspace.Paths.DiscoveryEntryPath).Should().Be(closeKind == 0);
            workspace.Root.Disposed.Should().BeFalse();
            running.IsCompleted.Should().BeFalse();
        }
        finally
        {
            workspace.Physical.Release(); workspace.CleanupGate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(running, stopping).ConfigureAwait(false);
        }
        outcomes.Should().OnlyContain(exception => exception == null);
        workspace.ListenerStops.Should().Be(1);
        workspace.Readiness.IsReady.Should().BeFalse();
        File.Exists(workspace.Paths.DiscoveryEntryPath).Should().BeFalse();
        workspace.Root.Disposed.Should().BeTrue();
        workspace.AssertFilesAvailable();
    }

    [TestCase(true), TestCase(false)]
    public async Task AuthorizedBindReplacementCannotPublishWithoutPhysicalStartupAsync(bool replacement)
    {
        var workspace = new Workspace(false, false, false, false, replacement);
        await using var disposal = workspace.ConfigureAwait(false);
        var running = workspace.RunAsync(CancellationToken.None);
        Task stopping = Task.CompletedTask;
        Task repeated = Task.CompletedTask;
        Exception?[] outcomes;
        try
        {
            await workspace.CleanupGate.WaitForEntryAsync().ConfigureAwait(false);
            stopping = workspace.StopAsync();
            repeated = workspace.StopAsync();
            AssertPending(workspace, running, stopping, repeated);
            workspace.Physical.Calls.Should().Be(0);
            workspace.ListenerStarted.Should().BeFalse();
        }
        finally
        {
            workspace.Physical.Release(); workspace.CleanupGate.Release();
            outcomes = await TestTaskOutcome.JoinAsync(running, stopping, repeated).ConfigureAwait(false);
        }
        var primary = outcomes[0].Should().BeOfType<KernelActionExecutionException>().Which;
        primary.Message.Should().Be("Runtime binding did not execute its required terminal.");
        outcomes[1].Should().BeSameAs(primary);
        outcomes[2].Should().BeSameAs(primary);
        workspace.ListenerStops.Should().Be(0);
        workspace.CredentialCleanups.Should().Be(1);
        workspace.Root.Disposed.Should().BeTrue();
        workspace.AssertFilesAvailable();
        workspace.Readiness.IsReady.Should().BeFalse();
        File.Exists(workspace.Paths.DiscoveryEntryPath).Should().BeFalse();
    }

    private static void AssertPending(Workspace workspace, params Task[] operations)
    {
        operations.Should().OnlyContain(task => !task.IsCompleted);
        workspace.Root.Disposed.Should().BeFalse();
        workspace.Root.Touch();
        workspace.Readiness.IsReady.Should().BeFalse();
        File.Exists(workspace.Paths.DiscoveryEntryPath).Should().BeFalse();
        workspace.CredentialCleanups.Should().Be(0);
        workspace.AssertRootFileHeld();
    }

    private sealed class BootstrapProbe(bool initialize, bool deadline, bool? replacement) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("bootstrap-held", "Held bootstrap", "test");
        public TaskCompletionSource ReceiptObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool? Replacement { get; } = replacement;
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(this);
            if (initialize)
            {
                services.AddSingleton<InitializeInterceptor>();
                services.OnAction(RuntimeStartupActionDefinitions.Initialize).Use<InitializeInterceptor>(Ordering());
            }
            else
            {
                services.AddSingleton<BindInterceptor>();
                services.OnAction(RuntimeLifecycleActionCatalog.StartBind).Use<BindInterceptor>(Ordering());
            }
        }
        private HookOrdering Ordering() => new("bootstrap-held", Timeout:
            deadline ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5));
    }

    private sealed class InitializeInterceptor(BootstrapProbe probe) : IActionInterceptor<string, bool>
    {
        public async ValueTask<IActionOutcome<bool>> InvokeAsync(ActionContext<string> context,
            IActionControl<string, bool> control, CancellationToken cancellationToken)
        {
            try { return await control.ProceedAsync(cancellationToken).ConfigureAwait(false); }
            finally { probe.ReceiptObserved.TrySetResult(); }
        }
    }

    private sealed class BindInterceptor(BootstrapProbe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            if (probe.Replacement is { } replacement)
                return control.ReplaceResult(replacement, "Authorized bind replacement without terminal execution.");
            try { return await control.ProceedAsync(cancellationToken).ConfigureAwait(false); }
            finally { probe.ReceiptObserved.TrySetResult(); }
        }
    }

    private sealed class RootDependency(string path) : IDisposable
    {
        private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        public bool Disposed { get; private set; }
        public void Touch() => _file.WriteByte(1);
        public void Dispose() { _file.Dispose(); Disposed = true; }
    }

    private sealed class HeldProvider(RootDependency root, ControlledOperationGate gate, string path)
        : ISharpClawPersistenceProvider, IAsyncDisposable
    {
        private FileStream? _resource;
        public string Key => "held-provider";
        public IReadOnlyCollection<string> Aliases { get; } = [];
        public bool IsRelational => false;
        public void Configure(DbContextOptionsBuilder optionsBuilder, SharpClawPersistenceProviderContext context) =>
            optionsBuilder.UseInMemoryDatabase(path);
        public async ValueTask InitializeAsync(DbContext dbContext, CancellationToken cancellationToken = default)
        {
            _resource = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try { await gate.RunAsync().ConfigureAwait(false); }
            finally { root.Touch(); }
            await dbContext.Database.EnsureCreatedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            if (_resource is not null) await _resource.DisposeAsync().ConfigureAwait(false);
            _resource = null;
        }
    }

    private sealed class HeldHostedService(RootDependency root, ControlledOperationGate gate) : IHostedLifecycleService
    {
        public int Stops { get; private set; }
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StartedAsync(CancellationToken cancellationToken)
        {
            try { await gate.RunAsync().ConfigureAwait(false); }
            finally { root.Touch(); }
        }
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sharpclaw-bootstrap-" + Guid.NewGuid().ToString("N"));
        private readonly bool _initialize;
        private readonly WebApplication _app;
        private readonly RuntimeKernelAdapter _adapter;
        private readonly RuntimeHostCleanup _cleanup;
        private readonly HeldHostedService _hosted;
        private bool _listenerAttempted;
        public BootstrapProbe Probe { get; }
        public ControlledOperationGate Physical { get; }
        public ControlledOperationGate CleanupGate { get; }
        public RootDependency Root { get; }
        public RuntimeReadinessState Readiness { get; } = new();
        public SharpClawInstancePaths Paths { get; }
        public TaskCompletionSource BindingReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BindingPublished { get; private set; }
        public bool ReadyAtBindingReturn { get; private set; }
        public bool DiscoveryAtBindingReturn { get; private set; }
        public int ListenerStops { get; private set; }
        public int CredentialCleanups { get; private set; }
        public bool ListenerStarted => _app.Lifetime.ApplicationStarted.IsCancellationRequested;

        public Workspace(bool initialize, bool deadline, bool lateFault, bool cleanupFault, bool? replacement = null)
        {
            WebApplication? constructing = null;
            try
            {
                _initialize = initialize;
                Paths = new(SharpClawInstanceKind.Backend, _directory, _directory, _directory);
                Paths.EnsureDirectories();
                Physical = new(lateFault ? new IOException("late bootstrap failure") : null);
                CleanupGate = new(cleanupFault ? new IOException("late listener cleanup failure") : null);
                Probe = new(initialize, deadline, replacement);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
                builder.Logging.ClearProviders();
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                { ["Database:Provider"] = "held-provider", ["Encryption:EncryptDatabase"] = "false" });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                foreach (var descriptor in TestServiceGraph.Collect([Probe])) builder.Services.Add(descriptor);
                builder.Services.AddSingleton(_ => new RootDependency(Path.Combine(_directory, "root")));
                builder.Services.AddSingleton<ISharpClawPersistenceProvider>(services =>
                    new HeldProvider(services.GetRequiredService<RootDependency>(), Physical, Path.Combine(_directory, "provider")));
                builder.Services.AddSingleton(services => new HeldHostedService(services.GetRequiredService<RootDependency>(), Physical));
                if (!initialize) builder.Services.AddHostedService(services => services.GetRequiredService<HeldHostedService>());
                var options = SharpClawPersistenceOptions.FromConfiguration(builder.Configuration, _directory);
                builder.Services.AddInfrastructure(builder.Configuration, options);
                _app = constructing = builder.Build();
                Root = _app.Services.GetRequiredService<RootDependency>();
                _hosted = _app.Services.GetRequiredService<HeldHostedService>();
                var key = initialize ? RuntimeStartupActionDefinitions.Initialize.Key : RuntimeLifecycleActionCatalog.StartBind;
                var capabilities = initialize ? RuntimeStartupActionDefinitions.Initialize.Capabilities : KernelActionCatalog.DescriptorFor(key).Capabilities;
                var compile = new KernelGraphCompileOptions
                {
                    ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
                    { [Probe.Identity.Id] = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal) { [key.Value] = capabilities } },
                };
                _adapter = new(builder.Configuration, _app.Services, new KernelJobsBindings(), [Probe], Paths, new NoProviders(), compile);
                _cleanup = new(Readiness.MarkNotReady, Paths.DeleteDiscoveryEntry, () => CredentialCleanups++, StopListenerAsync);
            }
            // No startup is admitted in this constructor; retire only this unstarted, context-free host.
            catch
            {
                if (constructing is not null) ((IDisposable)constructing).Dispose();
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
                throw;
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                if (_initialize)
                {
                    var database = new RuntimeDatabaseReadiness(_app.Services.GetRequiredService<IServiceScopeFactory>(),
                        _app.Services.GetRequiredService<PersistenceProviderSelection>(),
                        SharpClawPersistenceOptions.FromConfiguration(_app.Configuration, _directory));
                    await _adapter.RunRuntimeLifecycleActionAsync(RuntimeLifecycleActionCatalog.StartPrepare, null,
                        ct => new ValueTask(database.ValidateAsync(ct)), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _adapter.StartAsync("bootstrap-held", cancellationToken: cancellationToken).ConfigureAwait(false);
                    try
                    {
                        BindingPublished = await LocalRuntimeHost.BindApplicationAsync(_app, _adapter, Readiness, Paths, "http://127.0.0.1:1",
                            () => _listenerAttempted = true, cancellationToken).ConfigureAwait(false);
                        ReadyAtBindingReturn = Readiness.IsReady;
                        DiscoveryAtBindingReturn = File.Exists(Paths.DiscoveryEntryPath);
                    }
                    finally { BindingReturned.TrySetResult(); }
                }
            }
#pragma warning disable CA1031 // Transport the actual receipt error through production cleanup and rethrow after physical host retirement.
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
#pragma warning restore CA1031
            finally
            {
                failure = await LocalRuntimeHost.StopAndCleanUpAsync(_adapter, _cleanup, failure).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            failure?.Throw();
        }

        private async ValueTask StopListenerAsync()
        {
            try { await CleanupGate.RunAsync().ConfigureAwait(false); }
            finally
            {
                if (_listenerAttempted)
                {
                    ListenerStops++;
                    await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    _hosted.Stops.Should().Be(1);
                }
            }
        }
        public void RequestHostStop() => _app.Lifetime.StopApplication();
        public Task StopAsync() => _adapter.StopAsync(CancellationToken.None,
            _ => _cleanup.BeginAsync(), _ => _cleanup.CompleteAsync()).AsTask();
        public void AssertRootFileHeld()
        {
            Action open = () => { using var stream = File.Open(Path.Combine(_directory, "root"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); };
            open.Should().Throw<IOException>();
        }
        public void AssertFilesAvailable()
        {
            foreach (var file in _initialize ? new[] { "root", "provider" } : new[] { "root" })
            { using var stream = File.Open(Path.Combine(_directory, file), FileMode.Open, FileAccess.ReadWrite, FileShare.None); stream.CanRead.Should().BeTrue(); }
        }
        public async ValueTask DisposeAsync()
        {
            Physical.Release(); CleanupGate.Release();
            await _app.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => throw new AssertionException("Bootstrap controls must not construct an inference client.");
    }
}
