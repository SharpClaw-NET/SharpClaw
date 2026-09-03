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
            async (_, _) => await navigator.NavigateRouteAsync(
                sender,
                route,
                qualifier ?? string.Empty),
            cancellationToken);
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
            async (_, _) => await navigator.NavigateViewModelAsync<TViewModel>(
                sender,
                qualifier: qualifier ?? string.Empty,
                data: data),
            cancellationToken);
    }
}
