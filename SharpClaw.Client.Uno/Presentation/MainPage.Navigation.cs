using Microsoft.UI.Xaml;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage
{
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services)
            return;

        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Settings");
    }

    private void OnBootClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Boot");
    }

}
