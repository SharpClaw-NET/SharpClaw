using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class RemoteProxyLifecycleTests
{
    private static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(15);
    private const string SourceId = "remote-lifecycle-test";
    private static readonly string[] ActionNames =
    [
        "runtime.start.prepare", "runtime.start.configure", "runtime.start.bind",
        "runtime.stop.prepare", "runtime.stop.complete", "runtime.request.receive", "runtime.request.handler.invoke",
    ];

    [Test]
    public async Task RepeatedBindActionPublishesOneListenerAndSettlesOwnedShutdownAsync()
    {
        var probe = new LifecycleProbe("runtime.start.bind", "repeat");
        var rig = new LifecycleRig(probe);
        await using var rigDisposal = rig.ConfigureAwait(false);
        var run = rig.StartAsync();
        await ObserveAsync(probe.BindCompleted).ConfigureAwait(false);
        rig.StartedCount.Should().Be(1);
        probe.Actions.Where(static action => string.Equals(action.Key, "runtime.start.bind", StringComparison.Ordinal))
            .Select(static action => action.Attempt).Should().Equal(1, 2);
        File.Exists(rig.Paths.DiscoveryEntryPath).Should().BeTrue();
        File.Exists(rig.Paths.ApiKeyFilePath).Should().BeTrue();
        using var client = rig.CreateClient();
        using var response = await client.GetAsync(new Uri("/echo", UriKind.Relative),
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.IsSuccessStatusCode.Should().BeTrue();
        run.IsCompleted.Should().BeFalse();

        (await rig.StopAndJoinAsync().ConfigureAwait(false)).Should().BeNull();
        rig.StoppedCount.Should().Be(1);
        AssertSessionFilesRemoved(rig.Paths);
        await AssertListenerClosedAsync(client).ConfigureAwait(false);
    }

    [TestCase("cancel"), TestCase("replace")]
    public async Task RejectedBindCannotStartAListenerAndStillCleansConfiguredKeysAsync(string operation)
    {
        var probe = new LifecycleProbe("runtime.start.bind", operation);
        var rig = new LifecycleRig(probe);
        await using var rigDisposal = rig.ConfigureAwait(false);
        var failure = await CaptureOutcomeAsync(rig.StartAsync(), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        if (string.Equals(operation, "cancel", StringComparison.Ordinal))
            failure.Should().BeOfType<KernelActionCancelledException>();
        else
            failure.Should().BeOfType<KernelActionExecutionException>();
        rig.StartedCount.Should().Be(0);
        rig.App.Lifetime.ApplicationStarted.IsCancellationRequested.Should().BeFalse();
        probe.Actions.Select(static action => action.Key).Should().Equal(
            "runtime.start.prepare", "runtime.start.configure", "runtime.start.bind",
            "runtime.stop.prepare", "runtime.stop.complete");
        AssertSessionFilesRemoved(rig.Paths);
    }

    [TestCase("runtime.stop.prepare", "cancel"), TestCase("runtime.stop.prepare", "fail")]
    [TestCase("runtime.stop.complete", "cancel"), TestCase("runtime.stop.complete", "fail")]
    public async Task RejectedStopHookStillClosesListenerDiscoveryAndKeysAsync(string key, string operation)
    {
        var probe = new LifecycleProbe(key, operation);
        var rig = new LifecycleRig(probe);
        await using var rigDisposal = rig.ConfigureAwait(false);
        _ = rig.StartAsync();
        await ObserveAsync(probe.BindCompleted).ConfigureAwait(false);
        using var client = rig.CreateClient();
        using var response = await client.GetAsync(new Uri("/echo", UriKind.Relative),
            TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.IsSuccessStatusCode.Should().BeTrue();

        var failure = await rig.StopAndJoinAsync().ConfigureAwait(false);
        if (string.Equals(operation, "cancel", StringComparison.Ordinal))
            failure.Should().BeOfType<KernelActionCancelledException>();
        else
            failure.Should().BeOfType<KernelActionFailedException>();
        probe.Actions.Select(static action => action.Key).Should().Contain("runtime.stop.complete");
        rig.StoppedCount.Should().Be(1);
        AssertSessionFilesRemoved(rig.Paths);
        await AssertListenerClosedAsync(client).ConfigureAwait(false);
    }

    [Test]
    public async Task FailedCoreReceiptCannotReturnBeforePhysicalTerminalCleanupSettlesAsync()
    {
        var probe = new LifecycleProbe("runtime.request.handler.invoke", "fault-after-start");
        var rig = new LifecycleRig(probe);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var actions = rig.App.Services.GetRequiredService<RemoteProxyActionBoundary>();
        var work = actions.RunRequestAsync(CreateExecutionContext(), new RemoteProxyRequestInvocation("GET", "/gated"),
            token => RunPhysicalTerminalAsync(probe, token), caller.Token).AsTask();
        probe.OwnedTasks.Enqueue(work);
        try
        {
            await ObserveAsync(probe.TerminalEntered).ConfigureAwait(false);
            await ObserveAsync(probe.TerminalCancellation).ConfigureAwait(false);
            work.IsCompleted.Should().BeFalse("the terminal still owns its gated cleanup");
            probe.TerminalSettled.Task.IsCompleted.Should().BeFalse();
            probe.Release.TrySetResult();
            var failure = await CaptureOutcomeAsync(work, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            failure.Should().BeOfType<KernelActionFailedException>();
            ((KernelActionFailedException)failure!).Error.Code.Should().Be("ACTION_CONTINUATION_DENIED");
            probe.TerminalSettled.Task.IsCompleted.Should().BeTrue();
        }
        finally
        {
            probe.Release.TrySetResult();
            await caller.CancelAsync().ConfigureAwait(false);
            await probe.JoinOwnedTasksAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CancelledReceiptCannotAdmitADelayedTerminalAsync()
    {
        var probe = new LifecycleProbe("runtime.request.handler.invoke", "late");
        var rig = new LifecycleRig(probe);
        await using var rigDisposal = rig.ConfigureAwait(false);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        var terminalCalls = 0;
        var actions = rig.App.Services.GetRequiredService<RemoteProxyActionBoundary>();
        var work = actions.RunRequestAsync(CreateExecutionContext(), new RemoteProxyRequestInvocation("GET", "/late"), _ =>
        {
            Interlocked.Increment(ref terminalCalls);
            return ValueTask.CompletedTask;
        }, caller.Token).AsTask();
        probe.OwnedTasks.Enqueue(work);
        try
        {
            await ObserveAsync(probe.HookEntered).ConfigureAwait(false);
            await caller.CancelAsync().ConfigureAwait(false);
            (await CaptureOutcomeAsync(work, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().NotBeNull();
            terminalCalls.Should().Be(0);
            probe.Release.TrySetResult();
            await ObserveAsync(probe.HookSettled).ConfigureAwait(false);
            terminalCalls.Should().Be(0);
        }
        finally
        {
            probe.Release.TrySetResult();
            await caller.CancelAsync().ConfigureAwait(false);
            await probe.JoinOwnedTasksAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask RunPhysicalTerminalAsync(LifecycleProbe probe, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => probe.TerminalCancellation.TrySetResult()).ConfigureAwait(false);
        probe.TerminalEntered.TrySetResult();
        try
        {
            await ObserveAsync(probe.TerminalCancellation).ConfigureAwait(false);
            await ObserveAsync(probe.Release).ConfigureAwait(false);
            throw new IOException("Secondary physical cleanup failure.");
        }
        finally
        {
            probe.TerminalSettled.TrySetResult();
        }
    }

    private static void AssertSessionFilesRemoved(SharpClawInstancePaths paths)
    {
        File.Exists(paths.DiscoveryEntryPath).Should().BeFalse();
        File.Exists(paths.ApiKeyFilePath).Should().BeFalse();
        File.Exists(paths.GatewayTokenFilePath).Should().BeFalse();
    }

    private static async Task AssertListenerClosedAsync(HttpClient client)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
        cancellation.CancelAfter(ObservationBudget);
        Func<Task> request = async () =>
        {
            using var response = await client.GetAsync(new Uri("/echo", UriKind.Relative), cancellation.Token).ConfigureAwait(false);
        };
        await request.Should().ThrowAsync<HttpRequestException>().ConfigureAwait(false);
    }

    private static async Task ObserveAsync(TaskCompletionSource signal)
    {
#pragma warning disable VSTHRD003 // Fixture-owned completion signals schedule no work and have no UI/JoinableTask dependency; their producers are retained and joined before rig disposal.
        await signal.Task.WaitAsync(ObservationBudget, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "These controlled-failure tests capture owned tasks for failure assertions and cleanup joins; a timeout fails verification.")]
    private static async Task<Exception?> CaptureOutcomeAsync(Task work, CancellationToken cancellationToken)
    {
        try
        {
            await work.WaitAsync(ObservationBudget, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
    }

    private static KernelActionExecutionContext CreateExecutionContext() =>
        new(RequestPrincipal.Anonymous, ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid());

    private sealed class LifecycleRig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-proxy-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly LifecycleProbe _probe;
        private readonly CancellationTokenSource _stopping;
        private readonly CancellationTokenRegistration _startedRegistration;
        private readonly CancellationTokenRegistration _stoppedRegistration;
        private Task? _run;
        private int _startedCount;
        private int _stoppedCount;

        public LifecycleRig(LifecycleProbe probe)
        {
            _probe = probe;
            Paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, Path.Combine(_root, "backend"), _root);
            Paths.EnsureDirectories();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["ASPNETCORE_URLS"] = "http://127.0.0.1:0" }).Build();
            App = RemoteProxyHost.BuildApplication([], configuration, Paths,
                RemoteGatewayConnection.Create(new Uri("https://example.invalid"), null), services => RegisterHooks(services, probe));
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
            _startedRegistration = App.Lifetime.ApplicationStarted.Register(() => Interlocked.Increment(ref _startedCount));
            _stoppedRegistration = App.Lifetime.ApplicationStopped.Register(() => Interlocked.Increment(ref _stoppedCount));
        }

        public WebApplication App { get; }
        public SharpClawInstancePaths Paths { get; }
        public int StartedCount => Volatile.Read(ref _startedCount);
        public int StoppedCount => Volatile.Read(ref _stoppedCount);

        public Task StartAsync() => _run ??= RemoteProxyHost.RunApplicationAsync(App, Paths, _stopping.Token);

        public HttpClient CreateClient() => new()
        {
            BaseAddress = new Uri(App.Urls.Should().ContainSingle().Which),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        public async Task<Exception?> StopAndJoinAsync()
        {
            _probe.Release.TrySetResult();
            await _stopping.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // StartAsync started and retained this context-free loopback host run; its stop source is cancelled above and this bounded join finishes before app disposal.
            var failure = _run is null ? null : await CaptureOutcomeAsync(_run, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await _probe.JoinOwnedTasksAsync().ConfigureAwait(false);
            return failure;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAndJoinAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await App.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _startedRegistration.DisposeAsync().ConfigureAwait(false);
                    await _stoppedRegistration.DisposeAsync().ConfigureAwait(false);
                    _stopping.Dispose();
                    Directory.Delete(_root, recursive: true);
                }
            }
        }
    }

    private static void RegisterHooks(IServiceCollection services, LifecycleProbe probe)
    {
        services.AddLogging(static logging => logging.ClearProviders());
        services.AddSingleton(probe);
        services.AddScoped<LifecycleHook>();
        foreach (var name in ActionNames)
            services.AddSingleton(new ActionHookBinding(SourceId, BehaviorTargetKind.Exact, new SharpClawActionKey(name),
                null, typeof(LifecycleHook), false, new HookOrdering("observe-" + name), typeof(LifecycleHook).AssemblyQualifiedName!));
        services.AddSingleton(static provider => CreateObservedGraph(provider));
        services.AddSingleton(static provider => new KernelActionDispatcher(provider.GetRequiredService<KernelGraph>(),
            CreateExecutionContext(), repeatEvidenceAuthority: new MatchingRepeatEvidenceAuthority()));
    }

    private static KernelGraph CreateObservedGraph(IServiceProvider provider)
    {
        var grants = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
        var approvals = new List<KernelSensitiveActionApproval>();
        foreach (var name in ActionNames)
        {
            var key = new SharpClawActionKey(name);
            var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
            grants.Add(name, descriptor.Capabilities);
            if (!descriptor.ContainsSensitiveData)
                continue;
            var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
            approvals.Add(new KernelSensitiveActionApproval(SourceId, key, descriptor.Version,
                types.ActionType.AssemblyQualifiedName!, types.ResultType.AssemblyQualifiedName!, KernelSchemaIdentity.Action(descriptor)));
        }
        return new KernelGraphBuilder().Compile(provider, new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
            { [SourceId] = grants },
            SensitiveActionApprovals = approvals,
        });
    }

    private sealed class LifecycleProbe(string key, string operation)
    {
        public string Key { get; } = key;
        public string Operation { get; } = operation;
        public ConcurrentQueue<(string Key, int Attempt)> Actions { get; } = new();
        public ConcurrentQueue<Task> OwnedTasks { get; } = new();
        public TaskCompletionSource BindCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HookEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HookSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task JoinOwnedTasksAsync()
        {
            while (OwnedTasks.TryDequeue(out var work))
                _ = await CaptureOutcomeAsync(work, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class LifecycleHook(LifecycleProbe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            var work = InvokeCoreAsync(context, control, cancellationToken);
            probe.OwnedTasks.Enqueue(work);
            return new ValueTask<IActionOutcome<object>>(work);
        }

        private async Task<IActionOutcome<object>> InvokeCoreAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            probe.Actions.Enqueue((context.ActionKey.Value, context.Attempt));
            if (string.Equals(context.ActionKey.Value, probe.Key, StringComparison.Ordinal))
            {
                if (string.Equals(probe.Operation, "cancel", StringComparison.Ordinal))
                    return control.Cancel("PROXY_LIFECYCLE_CANCELLED", "Controlled lifecycle rejection.");
                if (string.Equals(probe.Operation, "fail", StringComparison.Ordinal))
                    return control.Fail(new ExecutionError("PROXY_LIFECYCLE_FAILED", "Controlled lifecycle failure."));
                if (string.Equals(probe.Operation, "replace", StringComparison.Ordinal))
                    return control.ReplaceResult(true, "Controlled attempt to bypass bind.");
                if (string.Equals(probe.Operation, "repeat", StringComparison.Ordinal) && context.Attempt == 1)
                    return await control.RepeatAsync(new ActionRepeatRequest<KernelActionEnvelope>(context.Action,
                        "Controlled repeated bind admission."), cancellationToken).ConfigureAwait(false);
                if (string.Equals(probe.Operation, "fault-after-start", StringComparison.Ordinal))
                {
                    probe.OwnedTasks.Enqueue(control.ProceedAsync(cancellationToken).AsTask());
                    await ObserveAsync(probe.TerminalEntered).ConfigureAwait(false);
                    throw new InvalidOperationException("Primary Core hook failure.");
                }
                if (string.Equals(probe.Operation, "late", StringComparison.Ordinal))
                {
                    var late = ProceedAfterReleaseAsync(control);
                    probe.OwnedTasks.Enqueue(late);
                    return await late.ConfigureAwait(false);
                }
            }
            var result = await control.ProceedAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(context.ActionKey.Value, "runtime.start.bind", StringComparison.Ordinal))
                probe.BindCompleted.TrySetResult();
            return result;
        }

        private async Task<IActionOutcome<object>> ProceedAfterReleaseAsync(IActionControl<KernelActionEnvelope, object> control)
        {
            probe.HookEntered.TrySetResult();
            try
            {
                await ObserveAsync(probe.Release).ConfigureAwait(false);
                return await control.ProceedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                probe.HookSettled.TrySetResult();
            }
        }
    }

    private sealed class MatchingRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
    {
        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<KernelActionRepeatEvidence?>(new("PROXY_TEST_REPEAT", request.RequiredKind,
                request.ActionKey, request.ActionVersion, request.IdempotencyScope, request.IdempotencyKey,
                request.PriorInvocationId, request.PriorAttempt, request.NextInvocationId, request.NextAttempt,
                request.RequestedAt, request.RequestedAt.AddMinutes(1)));
        }
    }
}
