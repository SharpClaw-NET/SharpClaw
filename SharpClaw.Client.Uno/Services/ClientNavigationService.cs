namespace SharpClaw.Services;

/// <summary>Routes Uno navigation commits through the client action boundary.</summary>
public sealed class ClientNavigationService(
    INavigator navigator,
    ClientActionDispatcher actions)
{
    public async ValueTask NavigateRouteAsync(
        object sender,
        string route,
        string? qualifier = null,
        CancellationToken cancellationToken = default)
    {
        await actions.NavigateAsync(
            route,
            qualifier,
            async (_, token) => await navigator.NavigateRouteAsync(
                sender,
                route,
                qualifier ?? string.Empty,
                cancellation: token).ConfigureAwait(true),
            cancellationToken).ConfigureAwait(true);
    }

    public async ValueTask NavigateViewModelAsync<TViewModel>(
        object sender,
        object? data = null,
        string? qualifier = null,
        CancellationToken cancellationToken = default)
    {
        var route = typeof(TViewModel).FullName ?? typeof(TViewModel).Name;
        await actions.NavigateAsync(
            route,
            qualifier,
            async (_, token) => await navigator.NavigateViewModelAsync<TViewModel>(
                sender,
                qualifier: qualifier ?? string.Empty,
                data: data,
                cancellation: token).ConfigureAwait(true),
            cancellationToken).ConfigureAwait(true);
    }
}
