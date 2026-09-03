namespace SharpClaw.Runtime.Host;

/// <summary>Tracks whether the authoritative Runtime passed startup readiness gates.</summary>
internal sealed class RuntimeReadinessState
{
    private int _ready;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public void MarkReady() => Interlocked.Exchange(ref _ready, 1);

    public void MarkNotReady() => Interlocked.Exchange(ref _ready, 0);
}
