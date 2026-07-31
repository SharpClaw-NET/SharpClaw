using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.RemoteRuntimeBridge;

internal enum RemoteRuntimeBridgeWorkKind
{
    Request,
    Stream,
    WebSocket,
    PairingControl,
}

internal sealed class RemoteRuntimeBridgeConcurrencyLimiter
{
    private const int MaximumTrackedPairs = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PairCounters> _pairs = [];
    private readonly int _requestLimit;
    private readonly int _streamLimit;
    private readonly int _webSocketLimit;
    private readonly GlobalCounters _global;

    public RemoteRuntimeBridgeConcurrencyLimiter(RemoteRuntimeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _requestLimit = options.MaxConcurrentRequestsPerPair;
        _streamLimit = options.MaxConcurrentStreamsPerPair;
        _webSocketLimit = options.MaxConcurrentWebSocketsPerPair;
        _global = new GlobalCounters(
            options.MaxConcurrentRequests,
            options.MaxConcurrentStreams,
            options.MaxConcurrentWebSockets,
            options.MaxConcurrentPairingControls);
    }

    public IDisposable? TryAcquire(Guid pairId, RemoteRuntimeBridgeWorkKind kind)
        => TryAcquire((Guid?)pairId, kind);

    public IDisposable? TryAcquire(Guid? pairId, RemoteRuntimeBridgeWorkKind kind)
    {
        if (pairId == Guid.Empty)
            return null;

        lock (_gate)
        {
            if (!_global.TryAcquire(kind))
                return null;

            if (pairId is { } actualPairId)
            {
                if (!_pairs.TryGetValue(actualPairId, out var counters))
                {
                    if (_pairs.Count >= MaximumTrackedPairs)
                    {
                        _global.Release(kind);
                        return null;
                    }

                    counters = new PairCounters();
                    _pairs.Add(actualPairId, counters);
                }

                if (!counters.TryAcquire(
                        kind,
                        _requestLimit,
                        _streamLimit,
                        _webSocketLimit))
                {
                    if (counters.IsEmpty)
                        _pairs.Remove(actualPairId);

                    _global.Release(kind);
                    return null;
                }
            }

            return new Lease(this, pairId, kind);
        }
    }

    private void Release(Guid? pairId, RemoteRuntimeBridgeWorkKind kind)
    {
        lock (_gate)
        {
            if (pairId is { } actualPairId
                && _pairs.TryGetValue(actualPairId, out var counters))
            {
                counters.Release(kind);
                if (counters.IsEmpty)
                    _pairs.Remove(actualPairId);
            }

            _global.Release(kind);
        }
    }

    private sealed class GlobalCounters
    {
        private readonly int _requestLimit;
        private readonly int _streamLimit;
        private readonly int _webSocketLimit;
        private readonly int _pairingControlLimit;
        private int _requests;
        private int _streams;
        private int _webSockets;
        private int _pairingControls;

        public GlobalCounters(
            int requestLimit,
            int streamLimit,
            int webSocketLimit,
            int pairingControlLimit)
        {
            _requestLimit = requestLimit;
            _streamLimit = streamLimit;
            _webSocketLimit = webSocketLimit;
            _pairingControlLimit = pairingControlLimit;
        }

        public bool TryAcquire(RemoteRuntimeBridgeWorkKind kind)
        {
            switch (kind)
            {
                case RemoteRuntimeBridgeWorkKind.Request:
                    if (_requests >= _requestLimit)
                        return false;
                    _requests++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.Stream:
                    if (_streams >= _streamLimit)
                        return false;
                    _streams++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.WebSocket:
                    if (_webSockets >= _webSocketLimit)
                        return false;
                    _webSockets++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.PairingControl:
                    if (_pairingControls >= _pairingControlLimit)
                        return false;
                    _pairingControls++;
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        public void Release(RemoteRuntimeBridgeWorkKind kind)
        {
            switch (kind)
            {
                case RemoteRuntimeBridgeWorkKind.Request:
                    if (_requests > 0)
                        _requests--;
                    break;
                case RemoteRuntimeBridgeWorkKind.Stream:
                    if (_streams > 0)
                        _streams--;
                    break;
                case RemoteRuntimeBridgeWorkKind.WebSocket:
                    if (_webSockets > 0)
                        _webSockets--;
                    break;
                case RemoteRuntimeBridgeWorkKind.PairingControl:
                    if (_pairingControls > 0)
                        _pairingControls--;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    private sealed class PairCounters
    {
        private int _requests;
        private int _streams;
        private int _webSockets;

        public bool IsEmpty => _requests == 0 && _streams == 0 && _webSockets == 0;

        public bool TryAcquire(
            RemoteRuntimeBridgeWorkKind kind,
            int requestLimit,
            int streamLimit,
            int webSocketLimit)
        {
            switch (kind)
            {
                case RemoteRuntimeBridgeWorkKind.Request:
                    if (_requests >= requestLimit)
                        return false;
                    _requests++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.Stream:
                    if (_streams >= streamLimit)
                        return false;
                    _streams++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.WebSocket:
                    if (_webSockets >= webSocketLimit)
                        return false;
                    _webSockets++;
                    return true;
                case RemoteRuntimeBridgeWorkKind.PairingControl:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        public void Release(RemoteRuntimeBridgeWorkKind kind)
        {
            switch (kind)
            {
                case RemoteRuntimeBridgeWorkKind.Request:
                    if (_requests > 0)
                        _requests--;
                    break;
                case RemoteRuntimeBridgeWorkKind.Stream:
                    if (_streams > 0)
                        _streams--;
                    break;
                case RemoteRuntimeBridgeWorkKind.WebSocket:
                    if (_webSockets > 0)
                        _webSockets--;
                    break;
                case RemoteRuntimeBridgeWorkKind.PairingControl:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    private sealed class Lease(
        RemoteRuntimeBridgeConcurrencyLimiter owner,
        Guid? pairId,
        RemoteRuntimeBridgeWorkKind kind) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(pairId, kind);
        }
    }
}
