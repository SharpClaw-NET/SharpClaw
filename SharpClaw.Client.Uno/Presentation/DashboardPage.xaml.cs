using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class DashboardPage : Page
{
    private static readonly Dictionary<string, string> CliCommands = new()
    {
        ["Providers"]     = "sharpclaw provider list",
        ["Models"]        = "sharpclaw model list",
        ["LocalModels"]   = "sharpclaw model list --local",
        ["Agents"]        = "sharpclaw agent list",
        ["Roles"]         = "sharpclaw role list",
        ["Jobs"]          = "sharpclaw job list",
        ["Channels"]      = "sharpclaw channel list",
        ["Contexts"]      = "sharpclaw ctx list",
        ["Resources"]     = "sharpclaw resource list",
        ["Editor"]        = "sharpclaw resource editor list",
        ["Auth"]          = "sharpclaw help",
    };

    public DashboardPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;

        var api = services.GetRequiredService<SharpClawApiClient>();
        StatusBlock.Text = $"connected to {api.BaseUrl}";
    }

    private void OnMenuItemClick(object? sender, string tag)
    {
        StatusBlock.Text = $"> {tag} (coming soon)";
    }

    private void OnMenuItemHover(object? sender, string tag)
    {
        if (CliCommands.TryGetValue(tag, out var command))
            Cursor.TypeCommand(command);
    }

    private void OnMenuItemLeave(object? sender, string tag)
    {
        Cursor.ClearCommand();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;

        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Main");
    }
}
