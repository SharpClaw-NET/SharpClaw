using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

internal sealed class FailingStartupDiagnosticsScope : IDisposable
{
    private readonly string _blocker;
    private int _fallbackCalls;
    internal ClientStartupDiagnostics Diagnostics { get; }
    internal int FallbackCalls => Volatile.Read(ref _fallbackCalls);

    internal FailingStartupDiagnosticsScope(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "io" => new IOException("Controlled stderr failure."),
            "access" => new UnauthorizedAccessException("Controlled stderr refusal."),
            "disposed" => new ObjectDisposedException("controlled-stderr"),
            _ => throw new ArgumentException("Unknown diagnostic sink failure.", nameof(failureKind)),
        };
        _blocker = Path.GetTempFileName();
        Diagnostics = new ClientStartupDiagnostics(Path.Combine(_blocker, "unavailable"), _ =>
        {
            Interlocked.Increment(ref _fallbackCalls);
            throw failure;
        });
    }

    public void Dispose()
    {
        File.Delete(_blocker);
        GC.SuppressFinalize(this);
    }
}
