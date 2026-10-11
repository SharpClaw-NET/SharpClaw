namespace SharpClaw.Services;

/// <summary>Context-free process settlement called only inside shared backend mutation ownership.</summary>
internal static class BundledBackendProcesses
{
    internal static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    internal static void RequireOwnedTarget(BackendProcessManager backend, SharpClawApiClient api)
    {
        BundledModuleSetup.RequireOwnedTarget(backend);
        if (!Uri.TryCreate(backend.ApiUrl, UriKind.Absolute, out var target) ||
            !Uri.TryCreate(api.BaseUrl, UriKind.Absolute, out var selected) || target != selected)
            throw new InvalidOperationException("Select this frontend's owned bundled Runtime before changing its configuration.");
    }

    internal static async Task StopAsync(BackendProcessManager backend, GatewayProcessManager? gateway,
        SharpClawApiClient api, CancellationToken cancellationToken)
    {
        RequireOwnedTarget(backend, api);
        cancellationToken.ThrowIfCancellationRequested();
        if (gateway is { IsExternal: false })
        {
            gateway.Stop();
            if (gateway.IsRunning) throw new InvalidOperationException("The owned local Gateway did not stop.");
        }
        backend.Stop();
        if (backend.IsRunning) throw new InvalidOperationException("The owned local Runtime did not stop.");
        await api.InvalidateApiKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task StartAsync(BackendProcessManager backend, GatewayProcessManager? gateway,
        SharpClawApiClient api, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await backend.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!backend.OwnsCurrentTarget)
            throw new InvalidOperationException("The local Runtime target is no longer owned by this frontend.");
        await api.WaitForReadyAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (gateway is { SkipLaunch: false, IsExternal: false, IsAvailable: true })
        {
            gateway.ApiKey = api.CachedApiKey;
            gateway.GatewayToken = null;
            await gateway.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
