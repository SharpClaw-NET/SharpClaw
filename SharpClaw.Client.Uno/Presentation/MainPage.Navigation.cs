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

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services)
            return;

        await services.GetRequiredService<ClientSessionService>().ClearAsync();
        await services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Login", Qualifiers.ClearBackStack);
    }

    private async void OnReportIssueClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://github.com/mkn8rn/SharpClaw/issues"));

    private async void OnOfficialWebsiteClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://sharpclaw.mkn8rn.com"));

    private async void OnMatrixCommunityClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://matrix.to/#/#p1:matrix.mkn8rn.com"));

    private async void OnCreatorBlogClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://blog.mkn8rn.com"));

    private void OnLegalNoticesClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services)
            return;

        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "LegalNotices");
    }

    private void OnUserGuideClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services)
            return;

        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "UserGuide");
    }
}
