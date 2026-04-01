using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class EnvMenuPage : Page
{
    /// <summary>Route name of the page that opened the env menu (for back navigation).</summary>
    public static string PendingOrigin { get; set; } = "Login";

    public EnvMenuPage()
    {
        InitializeComponent();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        var route = string.IsNullOrEmpty(PendingOrigin) ? "Login" : PendingOrigin;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, route);
    }

    private async void OnAppCoreClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;

        // Core env editing requires admin (or AllowNonAdmin override).
        if (!await CoreEnvGuard.IsAuthorisedAsync(services))
        {
            StatusBlock.Text = "✗ Admin login required to edit Application Core.";
            StatusBlock.Foreground = TerminalUI.Brush(0xFF4444);
            StatusBlock.Visibility = Visibility.Visible;
            return;
        }

        StatusBlock.Visibility = Visibility.Collapsed;
        EnvEditorPage.PendingTarget = EnvTarget.Core;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvEditor");
    }

    private void OnAppInterfaceClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        StatusBlock.Visibility = Visibility.Collapsed;
        EnvEditorPage.PendingTarget = EnvTarget.Interface;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvEditor");
    }

    private void OnGatewayClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        StatusBlock.Visibility = Visibility.Collapsed;
        EnvEditorPage.PendingTarget = EnvTarget.Gateway;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvEditor");
    }
}

public enum EnvTarget { Core, Interface, Gateway }
