using System.Net;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit constructs this internal TestFixture through reflection; its cases execute in the required CI domain.")]
internal sealed class PackagedSidecarReadinessTests
{
    [TestCase(null)]
    [TestCase("")]
    public void UnsetBudgetAllowsBoundedColdStartup(string? setting)
    {
        PackagedSidecarReadiness.ResolveTimeout(Configuration(setting))
            .Should().Be(TimeSpan.FromSeconds(120));
    }

    [TestCase("1", 1)]
    [TestCase("30", 30)]
    [TestCase("120", 120)]
    [TestCase("600", 600)]
    public void ExplicitBudgetIsRespected(string setting, int seconds)
    {
        PackagedSidecarReadiness.ResolveTimeout(Configuration(setting))
            .Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("601")]
    [TestCase("2147483648")]
    [TestCase("1.5")]
    [TestCase("30s")]
    [TestCase("NaN")]
    public void InvalidBudgetsFailClosed(string setting)
    {
        var act = () => PackagedSidecarReadiness.ResolveTimeout(Configuration(setting));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be an integer between 1 and 600*");
    }

    [Test]
    public async Task ColdSidecarCanBecomeReadyAfterTheFormerThirtySecondDeadline()
    {
        var clock = new ElapsedClock();
        var probes = 0;
        using var handler = new ProbeHandler((_, _) =>
        {
            probes++;
            clock.Advance(TimeSpan.FromSeconds(31));
            return Task.FromResult(new HttpResponseMessage(
                probes == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        });
        using var http = Client(handler);

        await PackagedSidecarReadiness.WaitAsync(http, () => false,
            PackagedSidecarReadiness.ResolveTimeout(Configuration(null)), CancellationToken.None, clock)
            .ConfigureAwait(false);

        probes.Should().Be(2);
    }

    [Test]
    public async Task ExplicitShortBudgetStillRejectsTheSameSlowSidecar()
    {
        var clock = new ElapsedClock();
        var probes = 0;
        using var handler = new ProbeHandler((_, _) =>
        {
            probes++;
            clock.Advance(TimeSpan.FromSeconds(31));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => false,
            TimeSpan.FromSeconds(30), CancellationToken.None, clock).ConfigureAwait(false);

        await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);
        probes.Should().Be(1);
    }

    [Test]
    public async Task ExitedProcessFailsWithoutWaitingOrProbing()
    {
        var probes = 0;
        using var handler = new ProbeHandler((_, _) =>
        {
            probes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => true,
            TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exited before readiness*")
            .ConfigureAwait(false);
        probes.Should().Be(0);
    }

    [Test]
    public async Task BootstrapBudgetCancelsAnInFlightProbe()
    {
        using var handler = new ProbeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => false,
            TimeSpan.FromMilliseconds(50), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        var failure = await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);
        failure.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Test]
    public async Task CallerCancellationIsNotReportedAsStartupTimeout()
    {
        using var caller = new CancellationTokenSource();
        using var handler = new ProbeHandler(async (_, token) =>
        {
            await caller.CancelAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => false,
            TimeSpan.FromSeconds(120), caller.Token).ConfigureAwait(false);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
    }

    [Test]
    public async Task RejectedReadinessStatusNeverGrantsReadiness()
    {
        var clock = new ElapsedClock();
        using var handler = new ProbeHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(121));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => false,
            TimeSpan.FromSeconds(120), CancellationToken.None, clock).ConfigureAwait(false);

        await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);
    }

    [Test]
    public async Task LateSuccessfulResponseCannotBypassTheBootstrapDeadline()
    {
        var clock = new ElapsedClock();
        using var handler = new ProbeHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(121));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var http = Client(handler);
        var act = async () => await PackagedSidecarReadiness.WaitAsync(http, () => false,
            TimeSpan.FromSeconds(120), CancellationToken.None, clock).ConfigureAwait(false);

        await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);
    }

    private static IConfiguration Configuration(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PackagedSidecarReadiness.TimeoutConfigurationKey] = value,
        }).Build();

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://127.0.0.1:1"),
        Timeout = TimeSpan.FromSeconds(2),
    };

    // Advance elapsed-time observations, not wall-clock time or a long-running
    // real sidecar. Production uses TimeProvider.System and its real timers.
    private sealed class ElapsedClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class ProbeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
