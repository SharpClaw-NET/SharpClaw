using Microsoft.UI.Xaml;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage
{
    private void OnSettingsClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (App.Services is not { } services) return;
        await services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Settings", cancellationToken: CancellationToken.None).ConfigureAwait(true);
    });

    private void OnBootClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (App.Services is not { } services) return;
        await services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Boot", cancellationToken: CancellationToken.None).ConfigureAwait(true);
    });

    private void OnRemoteConnectionClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (App.Services is not { } services) return;
        await services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "RemoteConnection", cancellationToken: CancellationToken.None).ConfigureAwait(true);
    });
}
