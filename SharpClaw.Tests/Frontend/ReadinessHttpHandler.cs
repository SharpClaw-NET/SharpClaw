using System.Net;

namespace SharpClaw.Tests.Frontend;

internal sealed class ReadinessHttpHandler(Exception? firstFailure, bool holdSuccess = true,
    bool ignoreCancellation = false, Exception? lateFailure = null) : HttpMessageHandler
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    internal TaskCompletionSource LaterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls => Volatile.Read(ref _calls);
    internal string? LastPath { get; private set; }
    internal string? LastKey { get; private set; }
    internal void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        LastPath = request.RequestUri?.AbsolutePath;
        var keys = request.Headers.GetValues("X-Api-Key").ToArray();
        keys.Should().HaveCount(1);
        LastKey = keys[0];
        if (call == 1 && firstFailure is not null) throw firstFailure;
        LaterEntered.TrySetResult();
        if (holdSuccess) await _release.Task.WaitAsync(TimeSpan.FromSeconds(5),
            ignoreCancellation ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        if (lateFailure is not null) throw lateFailure;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
