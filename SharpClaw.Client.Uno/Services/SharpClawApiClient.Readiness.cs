namespace SharpClaw.Services;

public sealed partial class SharpClawApiClient
{
    private async Task<bool> ProbeReadinessAsync(CancellationToken cancellationToken)
    {
        var target = GetTargetSnapshot();
        var admission = new Lock();
        var accepting = true;
        Task<bool>? physical = null;
        try
        {
            var receipt = await _clientActions.RunCommandAsync(
                new ClientCommandInvocation("http.send", "GET", "/ping", Guid.NewGuid(), "/ping"),
                (invocation, token) =>
                {
                    lock (admission)
                    {
                        if (!accepting || physical is not null)
                            throw new InvalidOperationException("This readiness probe may run only once.");
                        physical = Task.Run(() => SendReadinessProbeAsync(target, invocation, token), CancellationToken.None);
                        return new ValueTask<bool>(physical);
                    }
                }, cancellationToken).ConfigureAwait(false);
            Task<bool> owned;
            lock (admission)
            {
                accepting = false;
                owned = physical ?? throw new InvalidOperationException("The readiness probe was not admitted.");
            }
            // A receipt cannot certify readiness before its actual HTTP work.
#pragma warning disable VSTHRD003 // The retained probe runs on Task.Run without UI callbacks; joining it is required before returning readiness.
            var ready = await owned.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            return receipt && ready;
        }
        finally
        {
            Task<bool>? owned;
            lock (admission)
            {
                accepting = false;
                owned = physical;
            }
            if (owned is not null) await ObserveReadinessSettlementAsync(owned).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendReadinessProbeAsync(Uri target, ClientCommandInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = TryResolveApiKey(target);
        // Discovery and its key are published during process startup. Absence is
        // a negative probe, never permission to send an unauthenticated request.
        if (key is null) return false;
        using var request = new HttpRequestMessage(new HttpMethod(invocation.Method),
            ResolveRequestUri(target, invocation.EffectiveRequestTarget));
        request.Headers.Add("X-Api-Key", key);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        using (response)
        {
            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                await InvalidateApiKeyAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This finally join observes the one retained physical probe after its receipt failed or was cancelled. The original receipt remains primary; a successful receipt already awaits physical success. Only bounded exception metadata is journalled.")]
    private async Task ObserveReadinessSettlementAsync(Task<bool> physical)
    {
        try
        {
#pragma warning disable VSTHRD003 // The probe's Task.Run terminal is context-free and retained even when Core returns an interrupted receipt.
            await physical.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception failure) { (_startupDiagnostics ?? ClientStartupDiagnostics.Current).Record(ClientStartupStage.UnhandledException, failure); }
    }
}
