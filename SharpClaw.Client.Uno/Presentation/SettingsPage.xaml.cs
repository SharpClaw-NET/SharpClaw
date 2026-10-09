using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;
using System.Net.Http.Json;
using SharpClaw.Shared.Instances;

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
        Unloaded += (_, _) => { _settingsLifetime?.Cancel(); _tabLifetime?.Cancel(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Cursor.SetCommand("sharpclaw settings ");
        BuildTabs();
        SelectTab("Runtime");
        _settingsLifetime?.Cancel();
        _settingsLifetime?.Dispose();
        _settingsLifetime = new CancellationTokenSource();
        _ = LoadModuleTabsAsync(_settingsLifetime.Token);
    }

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        AddTabSection("Kernel");
        AddTabButton("Runtime", "sharpclaw runtime status");
        AddTabButton("Modules", "sharpclaw modules");
        AddTabButton("About", "sharpclaw notices");
    }

    private void AddTabSection(string title) => TabPanel.Children.Add(new TextBlock
    {
        Text = $"-- {title} --",
        FontFamily = Mono,
        FontSize = 10,
        Foreground = B(0x555555),
        Margin = new Thickness(8, 12, 0, 4),
    });

    private void AddTabButton(string label, string cursorCommand, string? tabId = null)
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
            Tag = tabId ?? label,
            Content = content,
        };
        button.Click += (_, _) => SelectTab(tabId ?? label);
        button.PointerEntered += (_, _) => Cursor.SetCommand(cursorCommand);
        button.PointerExited += (_, _) => Cursor.SetCommand("sharpclaw settings ");
        TabPanel.Children.Add(button);
    }

    private void SelectTab(string tab)
    {
        _activeTab = tab;
        _tabLifetime?.Cancel();
        _tabLifetime?.Dispose();
        _tabLifetime = new CancellationTokenSource();
        HighlightTabs();
        ContentPanel.Children.Clear();
        _ = LoadTabAsync(tab, _tabLifetime.Token);
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

    private async Task LoadRuntimeAsync(CancellationToken token)
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await Api.GetAsync("/readyz", timeout.Token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                status.Text = response.IsSuccessStatusCode
                    ? "ready"
                    : $"unavailable: HTTP {(int)response.StatusCode}";
                status.Foreground = B(response.IsSuccessStatusCode ? 0x00FF00 : 0xFF8800);
            }
            catch
            {
                if (token.IsCancellationRequested) return;
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
                await ApplyRuntimeTargetAsync(
                    Api,
                    Actions,
                    App.Services?.GetService<BackendProcessManager>(),
                    Gateway,
                    target,
                    TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
                endpoint.Text = Api.BaseUrl.TrimEnd('/');
                await RefreshAsync().ConfigureAwait(true);
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
        refresh.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        await RefreshAsync().ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        try { await LoadProviderSetupAsync(token).ConfigureAwait(true); }
        catch
        {
            token.ThrowIfCancellationRequested();
            Lbl("Provider setup information is unavailable; retry after checking Runtime status.", 0xFF8800);
        }
    }

    private async Task LoadProviderSetupAsync(CancellationToken token)
    {
        Sub("Provider setup");
        using var response = await Api.GetAsync("/setup/provider", token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            Lbl("Provider setup information is unavailable.", 0xFF8800);
            return;
        }
        var setup = await response.Content.ReadFromJsonAsync<SharpClawProviderSetup>(token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        if (setup is null) return;
        if (setup.Providers.Count == 0)
        {
            Lbl("No provider module is available. Return to Boot to install one.", 0x808080);
            return;
        }
        Lbl(setup.SetupRequired ? "Choose a provider and model to enable chat." : "Provider configured.", 0xCCCCCC);
        var backend = App.Services!.GetRequiredService<BackendProcessManager>();
        if (!backend.OwnsCurrentTarget || backend.SkipLaunch)
        {
            Lbl("Configure this external Runtime on its host; local settings will not be changed.", 0x808080);
            return;
        }
        var provider = new ComboBox
        {
            ItemsSource = setup.Providers,
            DisplayMemberPath = nameof(SharpClawProviderSetupOption.DisplayName),
            SelectedItem = setup.Providers.FirstOrDefault(item => item.Key == setup.ProviderKey),
            PlaceholderText = "Select an enabled provider",
            MinWidth = 320,
        };
        // Expose the actual selection even when the platform's item peers
        // have no accessible name. These are view bindings, not setters for
        // provider configuration or an alternate selection path.
        provider.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            new Microsoft.UI.Xaml.Data.Binding
            {
                Source = provider,
                Path = new PropertyPath($"{nameof(ComboBox.SelectedItem)}.{nameof(SharpClawProviderSetupOption.DisplayName)}"),
                FallbackValue = "AI provider",
                TargetNullValue = "AI provider",
            });
        provider.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.ItemStatusProperty,
            new Microsoft.UI.Xaml.Data.Binding
            {
                Source = provider,
                Path = new PropertyPath($"{nameof(ComboBox.SelectedItem)}.{nameof(SharpClawProviderSetupOption.Key)}"),
                FallbackValue = string.Empty,
                TargetNullValue = string.Empty,
            });
        var model = MakeInput("Model identifier");
        model.Text = setup.Model ?? string.Empty;
        SharpClawProviderModels? catalog = null;
        if (!setup.SetupRequired)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
            probe.CancelAfter(TimeSpan.FromSeconds(5));
            try { catalog = await StatelessChatReadiness.ReadAsync<SharpClawProviderModels>(Api, "/setup/models", probe.Token).ConfigureAwait(true); }
            catch { token.ThrowIfCancellationRequested(); }
        }
        token.ThrowIfCancellationRequested();
        var models = new ComboBox
        {
            ItemsSource = catalog?.Models,
            PlaceholderText = "Available models from the selected provider",
            MinWidth = 320,
            SelectedItem = catalog?.Models.FirstOrDefault(value => value == setup.Model),
            Visibility = catalog is null ? Visibility.Collapsed : Visibility.Visible,
        };
        models.SelectionChanged += (_, _) => { if (models.SelectedItem is string selectedModel) model.Text = selectedModel; };
        provider.SelectionChanged += (_, _) =>
        {
            models.Visibility = catalog is not null && provider.SelectedItem is SharpClawProviderSetupOption option &&
                string.Equals(option.Key, catalog.ProviderKey, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        };
        var endpoint = MakeInput("Optional provider endpoint (HTTP/HTTPS)");
        var credential = new PasswordBox { PlaceholderText = "API key or bearer token (if required)", MinWidth = 320 };
        var apply = TerminalButton("Save provider and restart bundled Runtime");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(provider, "ProviderSetupProvider");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(model, "ProviderSetupModel");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(endpoint, "ProviderSetupEndpoint");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(credential, "ProviderSetupCredential");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(apply, "ProviderSetupApply");
        var status = StatusBlock();
        ContentPanel.Children.Add(provider);
        ContentPanel.Children.Add(models);
        ContentPanel.Children.Add(model);
        ContentPanel.Children.Add(endpoint);
        ContentPanel.Children.Add(credential);
        ContentPanel.Children.Add(apply);
        ContentPanel.Children.Add(status);
        apply.Click += async (_, _) =>
        {
            if (provider.SelectedItem is not SharpClawProviderSetupOption selected ||
                string.IsNullOrWhiteSpace(model.Text))
            {
                status.Text = "Select a provider and enter its model identifier.";
                return;
            }
            apply.IsEnabled = false;
            try
            {
                await BundledProviderSetup.ApplyAsync(
                    App.Services!.GetRequiredService<FrontendInstanceService>(), backend,
                    Gateway, Actions, selected, model.Text, endpoint.Text, credential.Password, token).ConfigureAwait(true);
                credential.Password = string.Empty;
                await App.Services!.GetRequiredService<ClientNavigationService>()
                    .NavigateRouteAsync(this, "Boot", Qualifiers.ClearBackStack).ConfigureAwait(true);
            }
            catch
            {
                status.Text = "Setup failed. Check required credentials/endpoint and try again; no secrets are shown here.";
                status.Foreground = B(0xFF4444);
            }
            finally { apply.IsEnabled = true; }
        };
    }

    internal static async Task ApplyRuntimeTargetAsync(
        SharpClawApiClient api,
        ClientActionDispatcher actions,
        BackendProcessManager? backend,
        GatewayProcessManager? gateway,
        string target,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken = default)
    {
        await api.UpdateBaseUrlAsync(target, cancellationToken).ConfigureAwait(true);
        await actions.RunCommandAsync(
            "client.runtime.target",
            _ =>
            {
                backend?.UpdateApiUrl(target);
                gateway?.UpdateBackendBaseUrl(target);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(true);
        await api.WaitForReadyAsync(readinessTimeout, cancellationToken).ConfigureAwait(true);
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
            .NavigateRouteAsync(this, "Boot");
    }
}
