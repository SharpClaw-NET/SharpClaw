using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SharpClaw.Presentation;

public sealed partial class SettingsPage : Page
{
    private static FontFamily Mono => TerminalUI.Mono;
    private static SolidColorBrush Trans => TerminalUI.Transparent;

    private SharpClawApiClient Api =>
        App.Services!.GetRequiredService<SharpClawApiClient>();

    private ClientActionDispatcher Actions =>
        App.Services!.GetRequiredService<ClientActionDispatcher>();

    private GatewayProcessManager? Gateway =>
        App.Services?.GetService<GatewayProcessManager>();

    private string _activeTab = "Runtime";

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Cursor.SetCommand("sharpclaw settings ");
        BuildTabs();
        SelectTab("Runtime");
    }

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        AddTabSection("Kernel");
        AddTabButton("Runtime", "sharpclaw runtime status");
        AddTabSection("Gateway");
        AddTabButton("Gateway", "sharpclaw gateway status");
    }

    private void AddTabSection(string title) => TabPanel.Children.Add(new TextBlock
    {
        Text = $"-- {title} --",
        FontFamily = Mono,
        FontSize = 10,
        Foreground = B(0x555555),
        Margin = new Thickness(8, 12, 0, 4),
    });

    private void AddTabButton(string label, string cursorCommand)
    {
        var marker = new TextBlock
        {
            Text = ">",
            FontFamily = Mono,
            FontSize = 12,
        };
        var text = new TextBlock
        {
            Text = label,
            FontFamily = Mono,
            FontSize = 12,
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        content.Children.Add(marker);
        content.Children.Add(text);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Trans,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            Tag = label,
            Content = content,
        };
        button.Click += (_, _) => SelectTab(label);
        button.PointerEntered += (_, _) => Cursor.SetCommand(cursorCommand);
        button.PointerExited += (_, _) => Cursor.SetCommand("sharpclaw settings ");
        TabPanel.Children.Add(button);
    }

    private void SelectTab(string tab)
    {
        _activeTab = tab;
        HighlightTabs();
        ContentPanel.Children.Clear();

        _ = tab switch
        {
            "Runtime" => LoadRuntimeAsync(),
            "Gateway" => LoadGatewayAsync(),
            _ => Task.CompletedTask,
        };
    }

    private void HighlightTabs()
    {
        foreach (var child in TabPanel.Children)
        {
            if (child is not Button
                {
                    Tag: string tag,
                    Content: StackPanel { Children.Count: >= 2 } panel,
                })
            {
                continue;
            }

            var selected = tag == _activeTab;
            if (panel.Children[0] is TextBlock marker)
                marker.Foreground = B(selected ? 0x00FF00 : 0x555555);
            if (panel.Children[1] is TextBlock label)
                label.Foreground = B(selected ? 0xE0E0E0 : 0x999999);
        }
    }

    private async Task LoadRuntimeAsync()
    {
        H("Runtime");
        Lbl("Endpoint", 0x808080);

        var endpoint = MakeInput("http://127.0.0.1:48923");
        endpoint.Text = Api.BaseUrl.TrimEnd('/');
        endpoint.MinWidth = 320;
        ContentPanel.Children.Add(endpoint);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var apply = TerminalButton("Apply");
        var refresh = TerminalButton("Refresh");
        controls.Children.Add(apply);
        controls.Children.Add(refresh);
        ContentPanel.Children.Add(controls);

        var status = StatusBlock();
        ContentPanel.Children.Add(status);

        async Task RefreshAsync()
        {
            try
            {
                using var response = await Api.GetAsync("/readyz");
                status.Text = response.IsSuccessStatusCode
                    ? "ready"
                    : $"unavailable: HTTP {(int)response.StatusCode}";
                status.Foreground = B(response.IsSuccessStatusCode ? 0x00FF00 : 0xFF8800);
            }
            catch
            {
                status.Text = "unavailable";
                status.Foreground = B(0xFF4444);
            }
        }

        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            status.Text = "connecting";
            status.Foreground = B(0xFFCC00);
            try
            {
                var target = RequireHttpEndpoint(endpoint.Text);
                await Api.UpdateBaseUrlAsync(target);
                await Actions.RunCommandAsync(
                    "client.runtime.target",
                    _ =>
                    {
                        App.Services?.GetService<BackendProcessManager>()
                            ?.UpdateApiUrl(target);
                        Gateway?.UpdateBackendBaseUrl(target);
                        return ValueTask.CompletedTask;
                    });
                await Api.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                endpoint.Text = Api.BaseUrl.TrimEnd('/');
                await RefreshAsync();
            }
            catch
            {
                status.Text = "connection failed";
                status.Foreground = B(0xFF4444);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };
        refresh.Click += async (_, _) => await RefreshAsync();

        await RefreshAsync();
    }

    private async Task LoadGatewayAsync()
    {
        H("Gateway");

        var gateway = Gateway;
        if (gateway is null)
        {
            Lbl("unavailable", 0xFF4444);
            return;
        }

        Lbl("Endpoint", 0x808080);
        Lbl(gateway.ClientUrl, 0xCCCCCC);

        var status = StatusBlock();
        ContentPanel.Children.Add(status);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var start = TerminalButton("Start");
        var stop = TerminalButton("Stop");
        var restart = TerminalButton("Restart");
        var refresh = TerminalButton("Refresh");
        controls.Children.Add(start);
        controls.Children.Add(stop);
        controls.Children.Add(restart);
        controls.Children.Add(refresh);
        ContentPanel.Children.Add(controls);

        var persistent = new ToggleSwitch
        {
            IsOn = gateway.Persistent,
            OnContent = "Keep processes running",
            OffContent = "Stop processes on exit",
            FontFamily = Mono,
            FontSize = 11,
        };
        ContentPanel.Children.Add(persistent);

        Sub("Process output");
        var output = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 10,
            Foreground = B(0x888888),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 620,
        };
        ContentPanel.Children.Add(output);

        var logControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var copy = TerminalButton("Copy");
        var clear = TerminalButton("Clear");
        logControls.Children.Add(copy);
        logControls.Children.Add(clear);
        ContentPanel.Children.Add(logControls);

        async Task RefreshAsync()
        {
            var reachable = await Actions.RunCommandAsync(
                "client.gateway.status",
                token => new ValueTask<bool>(gateway.IsGatewayReachableAsync(token)));
            status.Text = reachable
                ? "online"
                : gateway.IsRunning
                    ? "starting"
                    : "offline";
            status.Foreground = B(reachable ? 0x00FF00 : 0xFF8800);
            output.Text = gateway.ProcessOutput.Count == 0
                ? "(no output)"
                : string.Join('\n', gateway.ProcessOutput);
            start.IsEnabled = !gateway.SkipLaunch && gateway.IsAvailable && !gateway.IsRunning;
            stop.IsEnabled = gateway.IsRunning && !gateway.IsExternal;
            restart.IsEnabled = !gateway.SkipLaunch && gateway.IsAvailable;
        }

        start.Click += async (_, _) =>
        {
            try
            {
                await Actions.RunCommandAsync(
                    "client.gateway.start",
                    async token =>
                    {
                        gateway.ApiKey = Api.CachedApiKey;
                        await gateway.EnsureStartedAsync(token);
                    });
            }
            catch
            {
                status.Text = "start failed";
                status.Foreground = B(0xFF4444);
            }
            await RefreshAsync();
        };

        stop.Click += async (_, _) =>
        {
            await Actions.RunCommandAsync(
                "client.gateway.stop",
                _ =>
                {
                    gateway.Stop();
                    return ValueTask.CompletedTask;
                });
            await RefreshAsync();
        };

        restart.Click += async (_, _) =>
        {
            try
            {
                await Actions.RunCommandAsync(
                    "client.gateway.restart",
                    async token =>
                    {
                        gateway.Stop();
                        await Task.Delay(250, token);
                        gateway.ApiKey = Api.CachedApiKey;
                        await gateway.EnsureStartedAsync(token);
                    });
            }
            catch
            {
                status.Text = "restart failed";
                status.Foreground = B(0xFF4444);
            }
            await RefreshAsync();
        };

        refresh.Click += async (_, _) => await RefreshAsync();

        persistent.Toggled += async (_, _) =>
        {
            var value = persistent.IsOn;
            await Actions.RunCommandAsync(
                "client.process.persistence",
                _ =>
                {
                    gateway.Persistent = value;
                    if (App.Services?.GetService<BackendProcessManager>() is { } backend)
                        backend.Persistent = value;
                    return ValueTask.CompletedTask;
                });
        };

        copy.Click += async (_, _) =>
        {
            await Actions.RunCommandAsync(
                "client.gateway.logs.copy",
                _ =>
                {
                    var package = new DataPackage();
                    package.SetText(output.Text ?? string.Empty);
                    Clipboard.SetContent(package);
                    return ValueTask.CompletedTask;
                });
        };

        clear.Click += async (_, _) =>
        {
            await Actions.RunCommandAsync(
                "client.gateway.logs.clear",
                _ =>
                {
                    gateway.ClearOutput();
                    return ValueTask.CompletedTask;
                });
            await RefreshAsync();
        };

        await RefreshAsync();
    }

    private static string RequireHttpEndpoint(string value)
    {
        var candidate = value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException("The Runtime endpoint must use HTTP or HTTPS.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private void H(string text) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 14,
        Foreground = B(0x00FF00),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    });

    private void Sub(string text) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 12,
        Foreground = B(0xBBBBBB),
        Margin = new Thickness(0, 8, 0, 0),
    });

    private void Lbl(string text, int color) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 11,
        Foreground = B(color),
        TextWrapping = TextWrapping.Wrap,
    });

    private static TextBlock StatusBlock() => new()
    {
        FontFamily = Mono,
        FontSize = 11,
        Foreground = B(0x808080),
    };

    private static TextBox MakeInput(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        FontFamily = Mono,
        FontSize = 12,
        Foreground = B(0xCCCCCC),
        Background = B(0x1A1A1A),
        BorderBrush = B(0x333333),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 6),
    };

    private static Button TerminalButton(string text) => new()
    {
        Content = text,
        FontFamily = Mono,
        FontSize = 11,
        Foreground = B(0x00FF00),
        Background = B(0x1A1A1A),
        BorderBrush = B(0x333333),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 5),
        MinWidth = 72,
    };

    private static SolidColorBrush B(int rgb) => TerminalUI.Brush(rgb);

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services)
            return;

        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Main");
    }
}
