using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class ClientApiReadinessTests
{
    private static readonly Uri Target = new("http://127.0.0.1:48923");

    [Test]
    public async Task StartupTransportFailureRetriesAndJoinsTheSuccessfulProbeAsync()
    {
        using var handler = new ReadinessHttpHandler(new HttpRequestException("Controlled connection refusal."));
        using var http = CreateHttp(handler);
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
            new ClientActionDispatcher(), fixedApiKey: "fixture-key");
        await using var apiDisposal = api.ConfigureAwait(false);
        var readiness = api.WaitForReadyAsync(TimeSpan.FromSeconds(10), TestContext.CurrentContext.CancellationToken);
        try
        {
            var winner = await Task.WhenAny(readiness, handler.LaterEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            winner.Should().BeSameAs(handler.LaterEntered.Task, "a transient refusal must not escape as a Core receipt failure");
            readiness.IsCompleted.Should().BeFalse("the admitted successful probe still owns physical work");
            handler.Release();
            await readiness.ConfigureAwait(false);
            handler.Calls.Should().Be(2);
            handler.LastPath.Should().Be("/ping");
            handler.LastKey.Should().Be("fixture-key");
        }
        finally
        {
            handler.Release();
            await ObserveRetainedReadinessAsync(readiness).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task MissingDiscoveryAndKeyRemainPendingUntilTheirPublicationAsync()
    {
        var discovery = new ReadinessDiscoveryScope();
        await using var discoveryDisposal = discovery.ConfigureAwait(false);
        using var handler = new ReadinessHttpHandler(null, holdSuccess: false);
        using var http = CreateHttp(handler);
        var probe = new ReadinessPhaseProbe();
        var actions = ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), probe);
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance, actions,
            frontendInstance: discovery.Frontend);
        await using var apiDisposal = api.ConfigureAwait(false);
        var readiness = api.WaitForReadyAsync(TimeSpan.FromSeconds(10), TestContext.CurrentContext.CancellationToken);
        try
        {
            var winner = await Task.WhenAny(readiness, probe.SecondPingDispatch.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            winner.Should().BeSameAs(probe.SecondPingDispatch.Task, "unpublished startup credentials require another admitted probe");
            readiness.IsCompleted.Should().BeFalse();
            handler.Calls.Should().Be(0, "no unauthenticated HTTP request may substitute for a missing key");
            await discovery.PublishAsync(Target).ConfigureAwait(false);
            await readiness.ConfigureAwait(false);
            handler.Calls.Should().Be(1);
            handler.LastKey.Should().Be(ReadinessDiscoveryScope.Key);
        }
        finally { await ObserveRetainedReadinessAsync(readiness).ConfigureAwait(false); }
    }

    [Test]
    public async Task CallerCancellationWhileCredentialsAreMissingStopsPollingAsync()
    {
        var discovery = new ReadinessDiscoveryScope();
        await using var discoveryDisposal = discovery.ConfigureAwait(false);
        using var handler = new ReadinessHttpHandler(null, holdSuccess: false);
        using var http = CreateHttp(handler);
        var probe = new ReadinessPhaseProbe();
        var actions = ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), probe);
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance, actions,
            frontendInstance: discovery.Frontend);
        await using var apiDisposal = api.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var readiness = api.WaitForReadyAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            var winner = await Task.WhenAny(readiness, probe.SecondPingDispatch.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
            winner.Should().BeSameAs(probe.SecondPingDispatch.Task);
            await cancellation.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // This delegate observes the retained readiness operation constructed by this test; cleanup joins it without a UI context.
            Func<Task> wait = () => readiness;
#pragma warning restore VSTHRD003
            await wait.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
            handler.Calls.Should().Be(0);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await ObserveRetainedReadinessAsync(readiness).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CoreDenialRemainsPrimaryAndCannotBecomeAPollingResultAsync()
    {
        using var handler = new ReadinessHttpHandler(null, holdSuccess: false);
        using var http = CreateHttp(handler);
        var probe = new ConfigurationOperationProbe("http.send");
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
            probe.CreateDispatcher(), fixedApiKey: "fixture-key");
        await using var apiDisposal = api.ConfigureAwait(false);
        Func<Task> wait = () => api.WaitForReadyAsync(TimeSpan.FromSeconds(10),
            TestContext.CurrentContext.CancellationToken);
        var failure = await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        failure.Which.Error.Should().BeSameAs(probe.Rejection);
        handler.Calls.Should().Be(0);
    }

    [Test]
    public async Task UnrelatedPhysicalFailureIsNotRetriedAsync()
    {
        using var handler = new ReadinessHttpHandler(new IOException("Controlled non-transport failure."));
        using var http = CreateHttp(handler);
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
            new ClientActionDispatcher(), fixedApiKey: "fixture-key");
        await using var apiDisposal = api.ConfigureAwait(false);
        Func<Task> wait = () => api.WaitForReadyAsync(TimeSpan.FromSeconds(10),
            TestContext.CurrentContext.CancellationToken);
        await wait.Should().ThrowAsync<KernelActionFailedException>().ConfigureAwait(false);
        handler.Calls.Should().Be(1);
    }

    private static HttpClient CreateHttp(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    { BaseAddress = Target };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Test cleanup joins the retained readiness task on success, assertion failure, fault or cancellation. Observing its exception must not replace the original assertion; only its type is written to the test trace.")]
    internal static async Task ObserveRetainedReadinessAsync(Task readiness)
    {
        try
        {
#pragma warning disable VSTHRD003 // The test owns this readiness task and joins it before transport/discovery disposal; its production probe retains context-free physical work.
            await readiness.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception failure) { await TestContext.Progress.WriteLineAsync(failure.GetType().Name).ConfigureAwait(false); }
    }
}
