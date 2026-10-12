using System.Runtime.ExceptionServices;

namespace SharpClaw.Services;

public sealed partial class SharpClawApiClient
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This HTTP-specific owner joins the actual send and retires only its untransferred response across receipt/completion failure. The first receipt failure is rethrown with its stack; secondary send/disposal failures are observed with bounded diagnostics.")]
    private async Task<HttpResponseMessage> RunOwnedHttpCommandAsync(ClientCommandInvocation invocation,
        Func<ClientCommandInvocation, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var admission = new Lock();
        var closed = false;
        Task<HttpResponseMessage>? physical = null;
        HttpResponseMessage? receipt = null;
        HttpResponseMessage? acquired = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            receipt = await _clientActions.RunCommandAsync(invocation, (effective, token) =>
            {
                lock (admission)
                {
                    if (closed) throw new InvalidOperationException("HTTP send admission has closed.");
                    physical ??= Task.Run(() => send(effective, token), CancellationToken.None);
                    return new ValueTask<HttpResponseMessage>(physical);
                }
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        Task<HttpResponseMessage>? owned;
        lock (admission) { closed = true; owned = physical; }
        if (owned is not null)
        {
            try
            {
#pragma warning disable VSTHRD003 // This exact context-free send is retained at admission; receipt interruption must not retire its borrowed dependencies early.
                acquired = await owned.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (Exception exception)
            {
                if (failure is null) failure = ExceptionDispatchInfo.Capture(exception);
                else (_startupDiagnostics ?? ClientStartupDiagnostics.Current).Record(ClientStartupStage.UnhandledException, exception);
            }
        }
        if (failure is null && acquired is not null && ReferenceEquals(receipt, acquired)) return acquired;
        failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("The HTTP receipt did not transfer its admitted response."));
        try { acquired?.Dispose(); }
        catch (Exception exception) { (_startupDiagnostics ?? ClientStartupDiagnostics.Current).Record(ClientStartupStage.UnhandledException, exception); }
        failure.Throw();
        throw new InvalidOperationException("Unreachable HTTP response transfer.");
    }
}
