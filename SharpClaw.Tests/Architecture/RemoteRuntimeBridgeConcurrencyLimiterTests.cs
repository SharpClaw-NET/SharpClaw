using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class RemoteRuntimeBridgeConcurrencyLimiterTests
{
    [Test]
    public void Each_work_kind_has_an_independent_limit_and_releases_idempotently()
    {
        var limiter = new RemoteRuntimeBridgeConcurrencyLimiter(
            new RemoteRuntimeBridgeOptions
            {
                MaxConcurrentRequestsPerPair = 1,
                MaxConcurrentStreamsPerPair = 1,
                MaxConcurrentWebSocketsPerPair = 1,
            });
        var pairId = Guid.NewGuid();

        using var request = limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.Request);
        using var stream = limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.Stream);
        using var webSocket = limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.WebSocket);

        request.Should().NotBeNull();
        stream.Should().NotBeNull();
        webSocket.Should().NotBeNull();
        limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.Request).Should().BeNull();
        limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.Stream).Should().BeNull();
        limiter.TryAcquire(pairId, RemoteRuntimeBridgeWorkKind.WebSocket).Should().BeNull();

        request!.Dispose();
        request.Dispose();
        var replacement = limiter.TryAcquire(
            pairId,
            RemoteRuntimeBridgeWorkKind.Request);
        replacement.Should().NotBeNull();
        replacement!.Dispose();
    }

    [Test]
    public void Pair_tracking_is_bounded_and_empty_entries_are_evicted()
    {
        var limiter = new RemoteRuntimeBridgeConcurrencyLimiter(
            new RemoteRuntimeBridgeOptions { MaxConcurrentRequestsPerPair = 1 });
        var leases = new List<IDisposable>();

        try
        {
            for (var index = 0; index < 1024; index++)
            {
                var lease = limiter.TryAcquire(
                    Guid.NewGuid(),
                    RemoteRuntimeBridgeWorkKind.Request);
                lease.Should().NotBeNull();
                leases.Add(lease!);
            }

            limiter.TryAcquire(Guid.NewGuid(), RemoteRuntimeBridgeWorkKind.Request)
                .Should().BeNull();

            leases[0].Dispose();
            var replacement = limiter.TryAcquire(
                Guid.NewGuid(),
                RemoteRuntimeBridgeWorkKind.Request);
            replacement.Should().NotBeNull();
            replacement!.Dispose();
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }
}
