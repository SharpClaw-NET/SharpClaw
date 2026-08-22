using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SharpClaw.Presentation;

public sealed partial class SettingsPage : Page
{
    private static JsonSerializerOptions Json => TerminalUI.Json;

    private static FontFamily Mono => TerminalUI.Mono;
    private static SolidColorBrush Trans => TerminalUI.Transparent;
    private SharpClawApiClient Api => App.Services!.GetRequiredService<SharpClawApiClient>();
    private ClientActionDispatcher Actions => App.Services!.GetRequiredService<ClientActionDispatcher>();

    private string _activeTab = "Providers";

    // Cached lists for cross-tab use
    private List<ProviderEntry>? _cachedProviders;
    private List<ProviderTypeEntry>? _cachedProviderTypes;
    private List<ModelEntry>? _cachedModels;

    // Current user info for admin tab
    private bool _isUserAdmin;

    // Gateway log console state
    private DispatcherTimer? _gatewayLogTimer;
    private TextBlock? _gatewayLogBlock;
    private ScrollViewer? _gatewayLogScroll;
    private int _gatewayLogSnapshot;

    // Module state for conditional UI
    private List<ModuleStateEntry>? _cachedModuleStates;

    // Module log console state
    private DispatcherTimer? _moduleLogTimer;
    private TextBlock? _moduleLogBlock;
    private ScrollViewer? _moduleLogScroll;
    private string? _moduleLogCursor;
    private string? _activeModuleLogId;

    public SettingsPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Cursor.SetCommand("sharpclaw settings ");
        await FetchCurrentUserInfoAsync();
        await RefreshModuleFrontendStateAsync();
        BuildTabs();
        SelectTab("Providers");
    }

    // ═══════════════════════════════════════════════════════════════
    // Sidebar tabs
    // ═══════════════════════════════════════════════════════════════


    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        AddTabSection("Models");
        AddTabButton("Providers", "sharpclaw provider list");
        AddTabButton("Models", "sharpclaw model list");
        AddTabSection("Gateway");
        AddTabButton("Gateway", "sharpclaw gateway status");
        AddTabSection("Modules");
        AddTabButton("Manage Modules", "sharpclaw module list");
        if (_cachedModuleStates is not null)
            foreach (var m in _cachedModuleStates)
                if (m.Enabled)
                    AddTabButton(m.DisplayName, $"sharpclaw module get {m.ModuleId}");
        if (_isUserAdmin)
        {
            AddTabSection("Admin");
            AddTabButton("Users", "sharpclaw user list");
            AddTabButton("Danger Zone", "sharpclaw reset");
        }
    }


    private void AddTabSection(string title) => TabPanel.Children.Add(new TextBlock
    {
        Text = $"── {title} ──", FontFamily = Mono, FontSize = 10,
        Foreground = B(0x555555), Margin = new Thickness(8, 12, 0, 4),
    });

    private void AddTabButton(string label, string cursorCmd)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Trans, BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8), Tag = label,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = "›", FontFamily = Mono, FontSize = 12,
            Foreground = B(label == _activeTab ? 0x00FF00 : 0x555555) });
        sp.Children.Add(new TextBlock { Text = label, FontFamily = Mono, FontSize = 12,
            Foreground = B(label == _activeTab ? 0xE0E0E0 : 0x999999) });
        btn.Content = sp;
        btn.Click += (_, _) => SelectTab(label);
        btn.PointerEntered += (_, _) => Cursor.SetCommand(cursorCmd);
        btn.PointerExited += (_, _) => Cursor.SetCommand("sharpclaw settings ");
        TabPanel.Children.Add(btn);
    }

    private void SelectTab(string tab)
    {

        _activeTab = tab;
        StopGatewayLogTimer();
        StopModuleLogTimer();
        HighlightTabs();
        ContentPanel.Children.Clear();

        _ = tab switch
        {
            "Providers" => LoadProvidersAsync(),
            "Models" => LoadModelsAsync(),
            "Gateway" => LoadGatewayAsync(),
            "Users" => LoadUsersAsync(),
            "Danger Zone" => LoadDangerZoneAsync(),
            "Manage Modules" => LoadManageModulesAsync(),
            _ => DispatchModuleTabAsync(tab),
        };
    }

    private void HighlightTabs()
    {
        foreach (var child in TabPanel.Children)
        {
            if (child is not Button { Tag: string tag, Content: StackPanel sp } || sp.Children.Count < 2) continue;
            var on = tag == _activeTab;
            if (sp.Children[0] is TextBlock a) a.Foreground = B(on ? 0x00FF00 : 0x555555);
            if (sp.Children[1] is TextBlock n) n.Foreground = B(on ? 0xE0E0E0 : 0x999999);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // PROVIDERS
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadProvidersAsync()
    {
        ContentPanel.Children.Clear();
        var (form, listPanel) = TabHeader("Providers", "Search providers…");
        _cachedProviders = await FetchListAsync<ProviderEntry>("/providers");
        _cachedProviderTypes = await FetchListAsync<ProviderTypeEntry>("/providers/types") ?? [];

        var nameBox = MakeInput("Provider name…");
        var typeBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 200 };
        foreach (var t in _cachedProviderTypes)
            typeBox.Items.Add(new ComboBoxItem { Content = $"{t.DisplayName} ({t.ProviderKey})", Tag = t });
        if (typeBox.Items.Count > 0) typeBox.SelectedIndex = 0;
        var epBox = MakeInput("https://... (optional endpoint)");
        epBox.Visibility = Visibility.Collapsed;
        typeBox.SelectionChanged += (_, _) =>
        {
            var selected = typeBox.SelectedItem is ComboBoxItem { Tag: ProviderTypeEntry t } ? t : null;
            epBox.Visibility = selected is { RequiresEndpoint: true } or { SupportsAutomaticEndpointDiscovery: true }
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var createBtn = GreenButton("Create");
        createBtn.Click += async (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var type = typeBox.SelectedItem is ComboBoxItem { Tag: ProviderTypeEntry t } ? t : null;
            if (type is null) return;
            if (string.IsNullOrEmpty(name)) return;
            var ep = (type.RequiresEndpoint || type.SupportsAutomaticEndpointDiscovery)
                ? epBox.Text.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(ep)) ep = null;
            var body = JsonSerializer.Serialize(new { name, providerKey = type.ProviderKey, apiEndpoint = ep }, Json);
            await Api.PostAsync("/providers", new StringContent(body, Encoding.UTF8, "application/json"));
            await LoadProvidersAsync();
        };
        form.Children.Add(nameBox);
        form.Children.Add(typeBox);
        form.Children.Add(epBox);
        form.Children.Add(createBtn);

        if (_cachedProviders is { Count: > 0 })
            foreach (var p in _cachedProviders)
                listPanel.Children.Add(MakeListRow(p.Name, p.ProviderKey,
                    () => ShowProviderDetail(p),
                    async () => { await Api.DeleteAsync($"/providers/{p.Id}"); await LoadProvidersAsync(); },
                    p.HasApiKey ? "✓ key" : "✗ no key", p.HasApiKey ? 0x00FF00 : 0xFF4444));
    }

    private void ShowProviderDetail(ProviderEntry p)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadProvidersAsync());
        H($"Provider: {p.Name}");
        Lbl($"type: {p.ProviderKey}   key: {(p.HasApiKey ? "✓ set" : "✗ not set")}", 0x999999);
        Lbl($"id: {p.Id}", 0x555555);

        var isDeviceCode = _cachedProviderTypes?.Any(t =>
            t.ProviderKey.Equals(p.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && t.SupportsDeviceCodeAuth) == true;

        if (isDeviceCode)
        {
            Sub("Device Code Login");
            var startBtn = GreenButton("[ Start Login ]");
            var codeBlock = new TextBlock { FontFamily = Mono, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = B(0x00FF00), IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
            var statusBlock = new TextBlock { FontFamily = Mono, FontSize = 11, Foreground = B(0x808080),
                Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            startBtn.Click += async (_, _) =>
            {
                startBtn.IsEnabled = false;
                statusBlock.Text = "Starting device code flow…"; statusBlock.Visibility = Visibility.Visible;
                try
                {
                    using var dcResp = await Api.PostAsync($"/providers/{p.Id}/auth/device-code", null);
                    if (!dcResp.IsSuccessStatusCode) { statusBlock.Text = "✗ Failed to start."; return; }
                    using var dcStream = await dcResp.Content.ReadAsStreamAsync();
                    using var dcDoc = await JsonDocument.ParseAsync(dcStream);
                    var userCode = dcDoc.RootElement.GetProperty("userCode").GetString()!;
                    var verUri = dcDoc.RootElement.GetProperty("verificationUri").GetString()!;
                    var deviceCode = dcDoc.RootElement.GetProperty("deviceCode").GetString()!;
                    var expires = dcDoc.RootElement.GetProperty("expiresInSeconds").GetInt32();
                    var interval = dcDoc.RootElement.GetProperty("intervalSeconds").GetInt32();
                    codeBlock.Text = userCode; codeBlock.Visibility = Visibility.Visible;
                    statusBlock.Text = $"Visit {verUri} and enter the code above.";
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri(verUri));
                    // Poll
                    var pollBody = JsonSerializer.Serialize(new { deviceCode, userCode, verificationUri = verUri, expiresInSeconds = expires, intervalSeconds = interval }, Json);
                    var pollResp = await Api.PostAsync($"/providers/{p.Id}/auth/device-code/poll",
                        new StringContent(pollBody, Encoding.UTF8, "application/json"));
                    if (pollResp.IsSuccessStatusCode)
                    { statusBlock.Text = "✓ Authenticated!"; statusBlock.Foreground = B(0x00FF00); }
                    else { statusBlock.Text = "✗ Authentication expired or failed."; statusBlock.Foreground = B(0xFF4444); }
                }
                catch (Exception ex) { statusBlock.Text = $"✗ {ex.Message}"; }
                finally { startBtn.IsEnabled = true; }
            };
            ContentPanel.Children.Add(startBtn);
            ContentPanel.Children.Add(codeBlock);
            ContentPanel.Children.Add(statusBlock);
        }
        else
        {
            Sub("Set API Key");
            var keyBox = new PasswordBox { FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00),
                Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
                PlaceholderText = "sk-…", MinWidth = 300, Padding = new Thickness(8, 6) };
            var setBtn = GreenButton("[ Set Key ]");
            var syncPlaceholder = new StackPanel();
            setBtn.Click += async (_, _) =>
            {
                var key = keyBox.Password.Trim();
                if (string.IsNullOrEmpty(key)) return;
                var body = JsonSerializer.Serialize(new { apiKey = key }, Json);
                var resp = await Api.PostAsync($"/providers/{p.Id}/set-key",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Status(resp.IsSuccessStatusCode ? "✓ API key set." : "✗ Failed to set key.",
                    resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
                if (resp.IsSuccessStatusCode && syncPlaceholder.Children.Count == 0)
                    AddProviderSyncSection(syncPlaceholder, p.Id);
            };
            ContentPanel.Children.Add(keyBox);
            ContentPanel.Children.Add(setBtn);
            ContentPanel.Children.Add(syncPlaceholder);
        }

        if (p.HasApiKey)
        {
            var syncContainer = new StackPanel();
            AddProviderSyncSection(syncContainer, p.Id);
            ContentPanel.Children.Add(syncContainer);
        }
    }

    private void AddProviderSyncSection(StackPanel container, Guid providerId)
    {
        container.Children.Add(new TextBlock
        {
            Text = "Sync Models", FontFamily = Mono, FontSize = 12,
            Foreground = B(0xBBBBBB), Margin = new Thickness(0, 8, 0, 0),
        });
        var syncBtn = GreenButton("↻ Sync models from provider");
        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false;
            try
            {
                var resp = await Api.PostAsync($"/providers/{providerId}/sync-models", null);
                Status(resp.IsSuccessStatusCode ? "✓ Models synced." : "✗ Sync failed.",
                    resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { syncBtn.IsEnabled = true; }
        };
        container.Children.Add(syncBtn);
    }

    // ═══════════════════════════════════════════════════════════════
    // MODELS
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadModelsAsync()
    {
        ContentPanel.Children.Clear();
        var (form, listPanel) = TabHeader("Models", "Search models…");
        _cachedModels = await FetchListAsync<ModelEntry>("/models");
        _cachedProviders ??= await FetchListAsync<ProviderEntry>("/providers");

        var keyedProviders = _cachedProviders?.Where(p => p.HasApiKey).ToList();
        if (keyedProviders is { Count: > 0 })
        {
            form.Children.Add(new TextBlock { Text = "Sync From Provider", FontFamily = Mono, FontSize = 12, Foreground = B(0xBBBBBB) });
            var provBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
                BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 240 };
            foreach (var prov in keyedProviders)
                provBox.Items.Add(new ComboBoxItem { Content = $"{prov.Name} ({prov.ProviderKey})", Tag = prov.Id });
            if (provBox.Items.Count > 0) provBox.SelectedIndex = 0;
            var syncBtn = GreenButton("↻ Sync");
            syncBtn.Click += async (_, _) =>
            {
                if (provBox.SelectedItem is not ComboBoxItem { Tag: Guid provId }) return;
                syncBtn.IsEnabled = false;
                try
                {
                    var resp = await Api.PostAsync($"/providers/{provId}/sync-models", null);
                    if (resp.IsSuccessStatusCode)
                    {
                        Status("✓ Models synced.", 0x00FF00);
                        await LoadModelsAsync();
                    }
                    else Status("✗ Sync failed.", 0xFF4444);
                }
                catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
                finally { syncBtn.IsEnabled = true; }
            };
            form.Children.Add(provBox);
            form.Children.Add(syncBtn);
        }

        form.Children.Add(new TextBlock { Text = "Add Local Model", FontFamily = Mono, FontSize = 12,
            Foreground = B(0xBBBBBB), Margin = new Thickness(0, 6, 0, 0) });
        var urlBox = MakeInput("HuggingFace model URL…");
        var listFilesBtn = GreenButton("[ List Files ]");
        var filePanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var fileBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 300 };
        var dlBtn = GreenButton("[ Download ]");
        var dlStatus = new TextBlock { FontFamily = Mono, FontSize = 11, Foreground = B(0x808080),
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        filePanel.Children.Add(fileBox);
        filePanel.Children.Add(dlBtn);
        listFilesBtn.Click += async (_, _) =>
        {
            var url = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            fileBox.Items.Clear();
            try
            {
                using var resp = await Api.GetAsync($"/models/local/download/list?url={Uri.EscapeDataString(url)}");
                if (resp.IsSuccessStatusCode)
                {
                    using var s = await resp.Content.ReadAsStreamAsync();
                    var files = await JsonSerializer.DeserializeAsync<List<ResolvedFile>>(s, Json);
                    if (files is { Count: > 0 })
                    {
                        foreach (var f in files)
                            fileBox.Items.Add(new ComboBoxItem { Content = f.Filename, Tag = f.DownloadUrl });
                        fileBox.SelectedIndex = 0;
                        filePanel.Visibility = Visibility.Visible;
                    }
                }
            }
            catch { Status("✗ Failed to list files.", 0xFF4444); }
        };
        dlBtn.Click += async (_, _) =>
        {
            if (fileBox.SelectedItem is not ComboBoxItem { Tag: string dlUrl }) return;
            dlBtn.IsEnabled = false;
            dlStatus.Text = "Downloading…"; dlStatus.Foreground = B(0x808080); dlStatus.Visibility = Visibility.Visible;
            try
            {
                var body = JsonSerializer.Serialize(new { url = dlUrl }, Json);
                var resp = await Api.PostAsync("/models/local/download",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                dlStatus.Text = resp.IsSuccessStatusCode ? "✓ Download started." : "✗ Failed.";
                dlStatus.Foreground = B(resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
            }
            catch (Exception ex) { dlStatus.Text = $"✗ {ex.Message}"; dlStatus.Foreground = B(0xFF4444); }
            finally { dlBtn.IsEnabled = true; }
        };
        form.Children.Add(urlBox);
        form.Children.Add(listFilesBtn);
        form.Children.Add(filePanel);
        form.Children.Add(dlStatus);

        if (_cachedModels is { Count: > 0 })
            foreach (var m in _cachedModels)
                listPanel.Children.Add(MakeListRow(m.Name, m.ProviderName, null,
                    async () => { await Api.DeleteAsync($"/models/{m.Id}"); await LoadModelsAsync(); }));
    }

    // ═══════════════════════════════════════════════════════════════
    private static string? LoadLocalSetting(string key)
        => App.Services?.GetService<ClientSettings>()?.Get(key);

    // ═══════════════════════════════════════════════════════════════
    // GATEWAY
    // ═══════════════════════════════════════════════════════════════

    private GatewayProcessManager? Gateway => App.Services?.GetService<GatewayProcessManager>();

    private ValueTask<bool> IsGatewayReachableAsync(
        GatewayProcessManager gateway,
        CancellationToken cancellationToken = default) =>
        Actions.RunCommandAsync(
            "client.gateway.health",
            token => new ValueTask<bool>(gateway.IsGatewayReachableAsync(token)),
            cancellationToken);

    private async Task LoadGatewayAsync()
    {
        ContentPanel.Children.Clear();
        H("Gateway");
        Lbl("Public entry point — handles security, rate-limiting, caching, and bot integrations.", 0x808080);

        var gw = Gateway;
        if (gw is null)
        {
            Status("GatewayProcessManager is not registered.", 0xFF4444);
            return;
        }

        // ── Status card ──────────────────────────────────────────
        var statusCard = new Border
        {
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Background = B(0x141414), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(16, 12),
        };
        var statusPanel = new StackPanel { Spacing = 6 };

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        statusRow.Children.Add(new TextBlock { Text = "🌐", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        statusRow.Children.Add(new TextBlock
        {
            Text = "Gateway Process", FontFamily = Mono, FontSize = 13,
            Foreground = B(0xE0E0E0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var statusIndicator = new TextBlock
        {
            FontFamily = Mono, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        statusRow.Children.Add(statusIndicator);
        statusPanel.Children.Add(statusRow);

        statusPanel.Children.Add(new TextBlock
        {
            Text = $"URL: {gw.ClientUrl}", FontFamily = Mono,
            FontSize = 10, Foreground = B(0x555555), IsTextSelectionEnabled = true,
        });

        var gwStatusBlock = new TextBlock
        {
            FontFamily = Mono, FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        statusPanel.Children.Add(gwStatusBlock);

        // Action buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0) };

        var startBtn = GreenButton("▶ Start");
        var stopBtn = new Button
        {
            Content = new TextBlock { Text = "■ Stop", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF8800) },
            Background = B(0x2A1A0A), BorderBrush = B(0xFF8800), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0),
        };
        var restartBtn = new Button
        {
            Content = new TextBlock { Text = "↻ Restart", FontFamily = Mono, FontSize = 12, Foreground = B(0x00CCFF) },
            Background = B(0x0A1A2A), BorderBrush = B(0x00CCFF), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0),
        };
        var refreshBtn = GreenButton("⟳ Refresh");

        btnRow.Children.Add(startBtn);
        btnRow.Children.Add(stopBtn);
        btnRow.Children.Add(restartBtn);
        btnRow.Children.Add(refreshBtn);
        statusPanel.Children.Add(btnRow);
        statusCard.Child = statusPanel;
        ContentPanel.Children.Add(statusCard);

        // ── Helper to apply visual state ─────────────────────────
        void ApplyState(bool online, bool running, bool external)
        {
            if (online)
            {
                statusIndicator.Text = external ? "● RUNNING (external)" : running ? "● RUNNING" : "● REACHABLE";
                statusIndicator.Foreground = B(0x00FF00);
                gwStatusBlock.Text = "Gateway is online.";
                gwStatusBlock.Foreground = B(0x00FF00);
                startBtn.IsEnabled = false;
                stopBtn.IsEnabled = !external && running;
            }
            else if (gw.SkipLaunch && !gw.IsAvailable)
            {
                statusIndicator.Text = "○ NOT ENABLED";
                statusIndicator.Foreground = B(0x666666);
                gwStatusBlock.Text = "Gateway launch is disabled and no bundled executable was found. "
                    + "Enable it in the environment settings or start it externally.";
                gwStatusBlock.Foreground = B(0xFF8800);
                startBtn.IsEnabled = false;
                stopBtn.IsEnabled = false;
                restartBtn.IsEnabled = false;
            }
            else
            {
                statusIndicator.Text = "○ OFFLINE";
                statusIndicator.Foreground = B(0xFF4444);

                if (gw.ExitCode is not null)
                    gwStatusBlock.Text = $"Gateway process exited with code {gw.ExitCode}.";
                else
                    gwStatusBlock.Text = gw.IsAvailable
                        ? "Gateway is not responding. Click Start to launch it."
                        : "Gateway executable not found. Start it externally or publish with /p:BundleGateway=true.";

                gwStatusBlock.Foreground = B(0xFF4444);
                stopBtn.IsEnabled = false;
                startBtn.IsEnabled = gw.IsAvailable && !gw.SkipLaunch;
            }
            restartBtn.IsEnabled = gw.IsAvailable && !gw.SkipLaunch;
        }

        // ── Probe current state ──────────────────────────────────
        var reachable = await IsGatewayReachableAsync(gw);
        ApplyState(reachable, gw.IsRunning, gw.IsExternal);

        // ── Log console ──────────────────────────────────────────
        var logHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Margin = new Thickness(0, 16, 0, 0),
        };
        logHeader.Children.Add(new TextBlock
        {
            Text = "Process Logs", FontFamily = Mono, FontSize = 12,
            Foreground = B(0xBBBBBB), VerticalAlignment = VerticalAlignment.Center,
        });

        var logCountBadge = new TextBlock
        {
            FontFamily = Mono, FontSize = 10, Foreground = B(0x555555),
            VerticalAlignment = VerticalAlignment.Center,
        };
        logHeader.Children.Add(logCountBadge);

        var copyBtn = new Button
        {
            Content = new TextBlock { Text = "Copy All", FontFamily = Mono, FontSize = 10, Foreground = B(0x00FF00) },
            Background = B(0x1A1A1A), BorderBrush = B(0x00FF00), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center,
        };
        logHeader.Children.Add(copyBtn);

        var clearBtn = new Button
        {
            Content = new TextBlock { Text = "Clear", FontFamily = Mono, FontSize = 10, Foreground = B(0x00FF00) },
            Background = B(0x1A1A1A), BorderBrush = B(0x00FF00), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center,
        };
        logHeader.Children.Add(clearBtn);
        ContentPanel.Children.Add(logHeader);

        var logScroll = new ScrollViewer
        {
            Background = B(0x0A0A0A),
            BorderBrush = B(0x333333),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            MinHeight = 180,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var logBlock = new TextBlock
        {
            FontFamily = Mono, FontSize = 10, Foreground = B(0x888888),
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        };
        logScroll.Content = logBlock;
        ContentPanel.Children.Add(logScroll);

        // Store references for timer-based refresh
        _gatewayLogBlock = logBlock;
        _gatewayLogScroll = logScroll;
        _gatewayLogSnapshot = 0;

        // Populate and schedule live refresh
        void RefreshLogConsole()
        {
            var lines = gw.ProcessOutput;
            if (lines.Count == _gatewayLogSnapshot)
                return; // nothing new

            _gatewayLogSnapshot = lines.Count;
            logBlock.Text = lines.Count > 0 ? string.Join('\n', lines) : "(no output)";
            logCountBadge.Text = $"{lines.Count} line{(lines.Count == 1 ? "" : "s")}";

            // Auto-scroll to bottom
            logScroll.UpdateLayout();
            logScroll.ChangeView(null, logScroll.ScrollableHeight, null, disableAnimation: true);
        }

        RefreshLogConsole();
        StartGatewayLogTimer(RefreshLogConsole);

        clearBtn.Click += async (_, _) =>
        {
            await Actions.RunCommandAsync(
                "client.gateway.logs.clear",
                _ =>
                {
                    gw.ClearOutput();
                    return ValueTask.CompletedTask;
                });
            _gatewayLogSnapshot = 0;
            logBlock.Text = "(no output)";
            logCountBadge.Text = "0 lines";
        };

        copyBtn.Click += async (_, _) =>
        {
            var text = logBlock.Text;
            if (!string.IsNullOrEmpty(text) && text != "(no output)")
            {
                var dp = new DataPackage();
                dp.SetText(text);
                Clipboard.SetContent(dp);
                ((TextBlock)copyBtn.Content).Text = "Copied ✓";
                await Task.Delay(1500);
                ((TextBlock)copyBtn.Content).Text = "Copy All";
            }
        };

        // ── Button handlers ──────────────────────────────────────
        startBtn.Click += async (_, _) =>
        {
            startBtn.IsEnabled = false;
            gwStatusBlock.Text = "Starting gateway…";
            gwStatusBlock.Foreground = B(0x808080);
            statusIndicator.Text = "◌ STARTING";
            statusIndicator.Foreground = B(0xFFCC00);
            try
            {
                await Actions.RunCommandAsync(
                    "client.gateway.start",
                    async token =>
                    {
                        gw.ApiKey = Api.CachedApiKey;
                        await gw.EnsureStartedAsync(token);
                    });
                var ready = false;
                for (var i = 0; i < 12; i++)
                {
                    await Task.Delay(500);
                    RefreshLogConsole();

                    if (!gw.IsRunning && !gw.IsExternal)
                    {
                        ApplyState(false, false, false);
                        gwStatusBlock.Text = $"✗ Gateway process exited (code {gw.ExitCode}).";
                        gwStatusBlock.Foreground = B(0xFF4444);
                        startBtn.IsEnabled = gw.IsAvailable && !gw.SkipLaunch;
                        return;
                    }
                    if (await IsGatewayReachableAsync(gw))
                    {
                        ready = true;
                        break;
                    }
                }

                if (ready)
                    ApplyState(true, gw.IsRunning, gw.IsExternal);
                else
                {
                    ApplyState(false, gw.IsRunning, gw.IsExternal);
                    gwStatusBlock.Text = "✗ Gateway started but is not responding yet.";
                    gwStatusBlock.Foreground = B(0xFF4444);
                    startBtn.IsEnabled = gw.IsAvailable && !gw.SkipLaunch;
                }
            }
            catch (Exception ex)
            {
                ApplyState(false, gw.IsRunning, gw.IsExternal);
                gwStatusBlock.Text = $"✗ {ex.Message}";
                gwStatusBlock.Foreground = B(0xFF4444);
                startBtn.IsEnabled = gw.IsAvailable && !gw.SkipLaunch;
            }
        };

        stopBtn.Click += async (_, _) =>
        {
            await Actions.RunCommandAsync(
                "client.gateway.stop",
                _ =>
                {
                    gw.Stop();
                    return ValueTask.CompletedTask;
                });
            ApplyState(false, false, false);
            gwStatusBlock.Text = "Gateway stopped.";
            gwStatusBlock.Foreground = B(0xFF8800);
        };

        restartBtn.Click += async (_, _) =>
        {
            restartBtn.IsEnabled = false;
            startBtn.IsEnabled = false;
            stopBtn.IsEnabled = false;
            gwStatusBlock.Text = "Restarting gateway…";
            gwStatusBlock.Foreground = B(0x808080);
            statusIndicator.Text = "◌ RESTARTING";
            statusIndicator.Foreground = B(0xFFCC00);
            try
            {
                await Actions.RunCommandAsync(
                    "client.gateway.restart",
                    async token =>
                    {
                        gw.Stop();
                        await Task.Delay(500, token);
                        gw.ApiKey = Api.CachedApiKey;
                        gw.Start();
                    });

                var ready = false;
                for (var i = 0; i < 12; i++)
                {
                    await Task.Delay(500);
                    RefreshLogConsole();

                    if (!gw.IsRunning && !gw.IsExternal)
                    {
                        ApplyState(false, false, false);
                        gwStatusBlock.Text = $"✗ Gateway process exited on restart (code {gw.ExitCode}).";
                        gwStatusBlock.Foreground = B(0xFF4444);
                        return;
                    }
                    if (await IsGatewayReachableAsync(gw))
                    {
                        ready = true;
                        break;
                    }
                }

                if (ready)
                    ApplyState(true, gw.IsRunning, gw.IsExternal);
                else
                {
                    ApplyState(false, gw.IsRunning, gw.IsExternal);
                    gwStatusBlock.Text = "✗ Gateway restarted but is not responding.";
                    gwStatusBlock.Foreground = B(0xFF4444);
                }
            }
            catch (Exception ex)
            {
                ApplyState(false, gw.IsRunning, gw.IsExternal);
                gwStatusBlock.Text = $"✗ {ex.Message}";
                gwStatusBlock.Foreground = B(0xFF4444);
            }
        };

        refreshBtn.Click += async (_, _) =>
        {
            refreshBtn.IsEnabled = false;
            var online = await IsGatewayReachableAsync(gw);
            ApplyState(online, gw.IsRunning, gw.IsExternal);
            RefreshLogConsole();
            refreshBtn.IsEnabled = true;
        };

        // ── Process Lifecycle settings ───────────────────────────
        BuildProcessLifecycleSection();
    }

    // ═══════════════════════════════════════════════════════════════
    // PROCESS LIFECYCLE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders the "Process Lifecycle" card inside the Gateway tab with
    /// toggles for persistent mode and Windows auto-start.
    /// </summary>
    private void BuildProcessLifecycleSection()
    {
        var backend = App.Services?.GetService<BackendProcessManager>();
        var gw = Gateway;

        // ── Section header ───────────────────────────────────────
        ContentPanel.Children.Add(new TextBlock
        {
            Text = "Process Lifecycle",
            FontFamily = Mono, FontSize = 13,
            Foreground = B(0xBBBBBB),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 24, 0, 0),
        });

        Lbl("Control whether backend and gateway survive frontend exit and auto-launch at login.", 0x808080);

        var card = new Border
        {
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Background = B(0x141414), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(16, 12),
        };
        var panel = new StackPanel { Spacing = 12 };

        // ── Persistent toggle ────────────────────────────────────
        var persistentToggle = new ToggleSwitch
        {
            IsOn = backend?.Persistent ?? false,
            OnContent = new TextBlock { Text = "Keep processes running on exit", FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00) },
            OffContent = new TextBlock { Text = "Stop processes on exit", FontFamily = Mono, FontSize = 11, Foreground = B(0x666666) },
        };
        var persistentStatus = new TextBlock { FontFamily = Mono, FontSize = 10, Foreground = B(0x555555), TextWrapping = TextWrapping.Wrap };
        persistentStatus.Text = persistentToggle.IsOn
            ? "Backend and gateway will remain running as background processes when you close the app."
            : "Backend and gateway will be stopped when you close the app.";

        persistentToggle.Toggled += async (_, _) =>
        {
            var on = persistentToggle.IsOn;
            await Actions.RunCommandAsync(
                "client.process.persistence",
                _ =>
                {
                    if (backend is not null) backend.Persistent = on;
                    if (gw is not null) gw.Persistent = on;
                    return ValueTask.CompletedTask;
                });

            persistentStatus.Text = on
                ? "Backend and gateway will remain running as background processes when you close the app."
                : "Backend and gateway will be stopped when you close the app.";
            persistentStatus.Foreground = on ? B(0x00FF00) : B(0x555555);

            Status(on ? "✓ Persistent mode enabled." : "Persistent mode disabled.", on ? 0x00FF00 : 0x808080);
        };
        panel.Children.Add(persistentToggle);
        panel.Children.Add(persistentStatus);

        // ── Windows auto-start toggle ────────────────────────────
        if (OperatingSystem.IsWindows())
        {
            panel.Children.Add(new Border
            {
                BorderBrush = B(0x222222), BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 4, 0, 4),
            });

            var autoStartEnabled = WindowsStartupManager.IsBackendAutoStartEnabled()
                                || WindowsStartupManager.IsGatewayAutoStartEnabled();

            var autoStartToggle = new ToggleSwitch
            {
                IsOn = autoStartEnabled,
                OnContent = new TextBlock { Text = "Launch at Windows login", FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00) },
                OffContent = new TextBlock { Text = "No auto-start", FontFamily = Mono, FontSize = 11, Foreground = B(0x666666) },
            };
            var autoStartStatus = new TextBlock { FontFamily = Mono, FontSize = 10, Foreground = B(0x555555), TextWrapping = TextWrapping.Wrap };
            autoStartStatus.Text = autoStartEnabled
                ? "Startup scripts registered in shell:startup. Works with both MSIX and unpackaged deployments."
                : "Processes only run when the app is open (unless persistent mode is on and they're already running).";

            autoStartToggle.Toggled += async (_, _) =>
            {
                var on = autoStartToggle.IsOn;
                await Actions.RunCommandAsync(
                    "client.autostart.update",
                    _ =>
                    {
                        if (backend is not null)
                            WindowsStartupManager.SetBackendAutoStart(
                                on && !backend.SkipLaunch,
                                backend.ExecutablePath,
                                backend.ApiUrl);

                        if (gw is not null)
                            WindowsStartupManager.SetGatewayAutoStart(
                                on && !gw.SkipLaunch,
                                gw.ExecutablePath,
                                gw.GatewayUrl);
                        return ValueTask.CompletedTask;
                    });

                autoStartStatus.Text = on
                    ? "Startup scripts registered in shell:startup. Works with both MSIX and unpackaged deployments."
                    : "Processes only run when the app is open (unless persistent mode is on and they're already running).";
                autoStartStatus.Foreground = on ? B(0x00FF00) : B(0x555555);

                Status(on ? "✓ Auto-start registered." : "Auto-start removed.", on ? 0x00FF00 : 0x808080);
            };
            panel.Children.Add(autoStartToggle);
            panel.Children.Add(autoStartStatus);
        }

        card.Child = panel;
        ContentPanel.Children.Add(card);
    }


    // ═══════════════════════════════════════════════════════════════

    private void StartGatewayLogTimer(Action refresh)
    {
        StopGatewayLogTimer();
        _gatewayLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gatewayLogTimer.Tick += (_, _) => refresh();
        _gatewayLogTimer.Start();
    }

    private void StopGatewayLogTimer()
    {
        _gatewayLogTimer?.Stop();
        _gatewayLogTimer = null;
        _gatewayLogBlock = null;
        _gatewayLogScroll = null;
    }
    // ═══════════════════════════════════════════════════════════════
    // Shared UI helpers
    // ═══════════════════════════════════════════════════════════════

    private (StackPanel Form, StackPanel List) TabHeader(string title, string searchHint)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Mono, FontSize = 14,
            Foreground = B(0x00FF00), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var form = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        var plus = new Button
        {
            Content = new TextBlock { Text = "+", FontFamily = Mono, FontSize = 16, Foreground = B(0x00FF00) },
            Background = B(0x2A2A2A), BorderBrush = B(0x444444), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2), MinWidth = 0, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(4),
        };
        plus.Click += (_, _) => form.Visibility = form.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        titleRow.Children.Add(plus);
        ContentPanel.Children.Add(titleRow);
        ContentPanel.Children.Add(form);

        ContentPanel.Children.Add(new Border
        {
            BorderBrush = B(0x333333), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 2),
        });

        var search = new TextBox
        {
            PlaceholderText = searchHint, FontFamily = Mono, FontSize = 12,
            Foreground = B(0xCCCCCC), Background = B(0x1A1A1A),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6), Margin = new Thickness(0, 2, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ContentPanel.Children.Add(search);

        var list = new StackPanel { Spacing = 2 };
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            foreach (var child in list.Children)
                if (child is FrameworkElement fe)
                    fe.Visibility = string.IsNullOrEmpty(q)
                        || (fe.Tag is string t && t.Contains(q, StringComparison.OrdinalIgnoreCase))
                        ? Visibility.Visible : Visibility.Collapsed;
        };
        ContentPanel.Children.Add(list);
        return (form, list);
    }

    private void H(string text) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 14,
        Foreground = B(0x00FF00), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    });

    private void Sub(string text) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 12,
        Foreground = B(0xBBBBBB), Margin = new Thickness(0, 8, 0, 0),
    });

    private void Lbl(string text, int color) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 11,
        Foreground = B(color), TextWrapping = TextWrapping.Wrap,
    });

    private void Status(string text, int color) => ContentPanel.Children.Add(new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 11,
        Foreground = B(color), Margin = new Thickness(0, 4, 0, 0),
    });

    private void BackLink(Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = "← Back", FontFamily = Mono, FontSize = 11, Foreground = B(0x808080) },
            Background = Trans, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 0, 4),
        };
        btn.Click += (_, _) => onClick();
        ContentPanel.Children.Add(btn);
    }

    private static TextBox MakeInput(string placeholder) => new()
    {
        PlaceholderText = placeholder, FontFamily = Mono, FontSize = 12,
        Foreground = B(0x00FF00), Background = B(0x1A1A1A),
        BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
        MinWidth = 260, Padding = new Thickness(8, 6),
    };

    private static Button GreenButton(string text) => new()
    {
        Content = new TextBlock { Text = text, FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) },
        Background = B(0x1A2A1A), BorderBrush = B(0x00FF00), BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0),
    };

    private Grid MakeListRow(string label, string? detail, Action? onClick, Func<Task>? onDelete,
        string? status = null, int statusColor = 0)
    {
        var row = new Grid { Tag = label };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Trans, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = "›", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) });
        sp.Children.Add(new TextBlock { Text = label, FontFamily = Mono, FontSize = 12, Foreground = B(0xE0E0E0) });
        if (detail is not null)
            sp.Children.Add(new TextBlock { Text = detail, FontFamily = Mono, FontSize = 10, Foreground = B(0x555555), VerticalAlignment = VerticalAlignment.Center });
        if (status is not null)
            sp.Children.Add(new TextBlock { Text = status, FontFamily = Mono, FontSize = 11, Foreground = B(statusColor),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (onClick is not null)
            sp.Children.Add(new TextBlock { Text = "✎", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00), VerticalAlignment = VerticalAlignment.Center });
        btn.Content = sp;
        if (onClick is not null) btn.Click += (_, _) => onClick();
        Grid.SetColumn(btn, 0);
        row.Children.Add(btn);
        if (onDelete is not null)
        {
            var del = new Button
            {
                Content = new TextBlock { Text = "✕", FontFamily = Mono, FontSize = 10, Foreground = B(0xFF4444) },
                Background = Trans, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
            };
            del.Click += async (_, _) => await onDelete();
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
        }
        return row;
    }

    private Task<List<T>?> FetchListAsync<T>(string path) => Api.FetchListAsync<T>(path, Json);

    private static SolidColorBrush B(int rgb) => TerminalUI.Brush(rgb);

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Main");
    }

    private void OnEnvClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        EnvMenuPage.PendingOrigin = "Settings";
        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "EnvMenu");
    }

    // ═══════════════════════════════════════════════════════════════
    // Current user info
    // ═══════════════════════════════════════════════════════════════

    private async Task FetchCurrentUserInfoAsync()
    {
        try
        {
            using var resp = await Api.GetAsync("/auth/me");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                _isUserAdmin = doc.RootElement.TryGetProperty("isUserAdmin", out var adminProp)
                    && adminProp.GetBoolean();
            }
        }
        catch { /* swallow */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // USERS (admin only)
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadUsersAsync()
    {
        ContentPanel.Children.Clear();
        H("Users");
        Lbl("Manage registered users. Requires admin.", 0x808080);

        List<UserListEntry>? users = null;
        try
        {
            using var resp = await Api.GetAsync("/users");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                users = await JsonSerializer.DeserializeAsync<List<UserListEntry>>(s, Json);
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Lbl("✗ You do not have admin privileges.", 0xFF4444);
                return;
            }
        }
        catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); return; }

        if (users is not { Count: > 0 })
        {
            Lbl("No users found.", 0x808080);
            return;
        }

        Sub("Registered Users");
        var list = new StackPanel { Spacing = 8 };
        foreach (var u in users)
        {
            var userRow = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new TextBlock { Text = "›", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) });
            headerRow.Children.Add(new TextBlock { Text = u.Username, FontFamily = Mono, FontSize = 12,
                Foreground = B(0xE0E0E0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (u.IsUserAdmin)
                headerRow.Children.Add(new TextBlock { Text = "admin", FontFamily = Mono, FontSize = 10,
                    Foreground = B(0xFFCC00), VerticalAlignment = VerticalAlignment.Center });
            userRow.Children.Add(headerRow);

            // Show user ID in muted text
            userRow.Children.Add(new TextBlock { Text = $"id: {u.Id}", FontFamily = Mono, FontSize = 9,
                Foreground = B(0x444444), Margin = new Thickness(16, 0, 0, 0) });

            list.Children.Add(userRow);
        }
        ContentPanel.Children.Add(list);
    }

    // ── DTOs ────────────────────────────────────────────────────

    [ImplicitKeys(IsEnabled = false)]
    private sealed record ProviderEntry(Guid Id, string Name, string ProviderKey, string? ApiEndpoint, bool HasApiKey);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ProviderTypeEntry(
        string ProviderKey,
        string DisplayName,
        bool RequiresEndpoint,
        bool SupportsAutomaticEndpointDiscovery,
        bool RequiresApiKey,
        bool SupportsDeviceCodeAuth);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ModelEntry(Guid Id, string Name, Guid ProviderId, string ProviderName, string Capabilities);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ResolvedFile(string DownloadUrl, string Filename, string? Quantization);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record UserListEntry(Guid Id, string Username, string? Bio, bool IsUserAdmin);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ModuleStateEntry(
        string ModuleId, string DisplayName, string ToolPrefix,
        bool Enabled, string? Version, bool Registered, bool IsExternal,
        DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

    // ═══════════════════════════════════════════════════════════════
    // DANGER ZONE
    // ═══════════════════════════════════════════════════════════════

    private Task LoadDangerZoneAsync()
    {
        ContentPanel.Children.Clear();
        H("Danger Zone");
        Lbl("Irreversible actions that destroy local data.", 0xFF4444);

        ContentPanel.Children.Add(new Border
        {
            BorderBrush = B(0x331111), BorderThickness = new Thickness(1),
            Background = B(0x1A0A0A), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 12),
            Child = BuildResetSection(),
        });

        return Task.CompletedTask;
    }

    private StackPanel BuildResetSection()
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = "Reset Current Stack", FontFamily = Mono, FontSize = 13,
            Foreground = B(0xFF4444), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Permanently deletes the current frontend instance, its companion gateway state, "
                 + "and requests the currently selected backend instance to factory reset its own data. "
                 + "The application will restart as if freshly installed for this stack.",
            FontFamily = Mono, FontSize = 11, Foreground = B(0xBBBBBB),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This action cannot be undone.",
            FontFamily = Mono, FontSize = 11, Foreground = B(0xFF6666),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var confirmPanel = new StackPanel { Visibility = Visibility.Collapsed, Spacing = 8 };
        confirmPanel.Children.Add(new TextBlock
        {
            Text = "Type RESET to confirm:", FontFamily = Mono, FontSize = 11,
            Foreground = B(0xFF8888),
        });
        var confirmBox = MakeInput("RESET");
        confirmBox.MaxWidth = 200;
        confirmPanel.Children.Add(confirmBox);

        var executeBtn = new Button
        {
            Content = new TextBlock { Text = "[ Confirm Reset ]", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF4444) },
            Background = B(0x2A1111), BorderBrush = B(0xFF4444), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), IsEnabled = false,
        };
        confirmBox.TextChanged += (_, _) =>
            executeBtn.IsEnabled = string.Equals(confirmBox.Text.Trim(), "RESET", StringComparison.Ordinal);
        executeBtn.Click += async (_, _) => await ExecuteFullResetAsync();
        confirmPanel.Children.Add(executeBtn);

        var showBtn = new Button
        {
            Content = new TextBlock { Text = "[ Reset Current Stack ]", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF4444) },
            Background = B(0x2A1111), BorderBrush = B(0xFF4444), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0),
        };
        showBtn.Click += (_, _) =>
        {
            showBtn.Visibility = Visibility.Collapsed;
            confirmPanel.Visibility = Visibility.Visible;
        };

        panel.Children.Add(showBtn);
        panel.Children.Add(confirmPanel);
        return panel;
    }

    private async Task ExecuteFullResetAsync()
    {
        ContentPanel.Children.Clear();
        H("Resetting…");
        Lbl("Resetting the current frontend instance and selected backend instance…", 0xFF8888);

        await Task.Delay(200); // Let UI render

        var errors = new List<string>();

        // 1. Tell the backend to purge its own Data and Environment directories
        //    while the process is still running and knows its own paths.
        try
        {
            var api = App.Services?.GetService<SharpClawApiClient>();
            if (api is not null)
            {
                using var resp = await api.PostAsync("/system/factory-reset", null);
                if (!resp.IsSuccessStatusCode)
                    errors.Add($"Factory reset API: HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex) { errors.Add($"Factory reset API: {ex.Message}"); }

        // 2. Stop the backend and gateway processes.
        try
        {
            var gateway = App.Services?.GetService<GatewayProcessManager>();
            if (gateway is not null)
            {
                await Actions.RunCommandAsync(
                    "client.gateway.stop",
                    _ =>
                    {
                        gateway.Dispose();
                        return ValueTask.CompletedTask;
                    });
            }
        }
        catch { /* best-effort */ }

        try
        {
            var backend = App.Services?.GetService<BackendProcessManager>();
            if (backend is not null)
            {
                await Actions.RunCommandAsync(
                    "client.backend.stop",
                    _ =>
                    {
                        backend.Stop();
                        return ValueTask.CompletedTask;
                    });
            }
        }
        catch (Exception ex) { errors.Add($"Stop backend: {ex.Message}"); }

        // 3. Clear frontend-only preferences (client-settings.json) in memory
        //    so they are not re-flushed to disk before the directory is deleted.
        try
        {
            var settings = App.Services?.GetService<ClientSettings>();
            if (settings is not null)
                await settings.ResetAsync();
        }
        catch (Exception ex) { errors.Add($"Client settings: {ex.Message}"); }

        // 3b. Clear saved account store.
        try
        {
            var accounts = App.Services?.GetService<AccountStore>();
            if (accounts is not null)
                await accounts.ResetAsync();
        }
        catch (Exception ex) { errors.Add($"Account store: {ex.Message}"); }

        // 4. Clean only the frontend-owned instance root. This also removes
        //    any bundled gateway companion state that lives under the current
        //    frontend stack. Backend-owned auth files and backend discovery
        //    entries are handled by backend reset/shutdown instead.
        try
        {
            var frontend = App.Services?.GetService<FrontendInstanceService>();
            if (frontend is not null)
            {
                await Actions.RunCommandAsync(
                    "client.factory-reset.files",
                    token => new ValueTask(
                        DeleteWithRetryAsync(
                            frontend.Paths.InstanceRoot,
                            "Frontend instance",
                            errors,
                            token)));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Frontend instance: {ex.Message}");
        }

        // 5. Invalidate the client's cached API key so the next request
        //    re-reads from disk (handles both external and bundled restarts).
        try
        {
            var api = App.Services?.GetService<SharpClawApiClient>();
            if (api is not null)
                await api.InvalidateApiKeyAsync();
        }
        catch { /* best-effort */ }

        // Show result
        ContentPanel.Children.Clear();
        if (errors.Count == 0)
        {
            H("Reset Complete");
            Lbl("All local data has been deleted. The application will now restart.", 0x00FF00);
        }
        else
        {
            H("Reset Completed with Warnings");
            foreach (var err in errors)
                Lbl($"⚠ {err}", 0xFFCC00);
            Lbl("Some files could not be deleted. They may be locked by another process.", 0xFF8888);
        }

        await Task.Delay(1500);

        // Navigate back to boot page so the app restarts the connection flow.
        if (App.Services is { } services)
        {
            await services.GetRequiredService<ClientNavigationService>()
                .NavigateRouteAsync(this, "Boot", Qualifiers.ClearBackStack);
        }
    }

    private static async Task DeleteWithRetryAsync(
        string path,
        string label,
        List<string> errors,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path)) return;

        const int maxAttempts = 3;
        for (int i = 1; i <= maxAttempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (i < maxAttempts)
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes all files and subdirectories inside <paramref name="path"/>
    /// except <c>.api-key</c> and <c>.gateway-token</c>, which are owned by
    /// the backend process and must survive a factory reset so the client can
    /// re-authenticate against a still-running (external/dev) backend.
    /// </summary>
    private static async Task CleanDirectoryPreservingAuthFilesAsync(
        string path,
        List<string> errors,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            return;

        HashSet<string> preserve = [".api-key", ".gateway-token"];

        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (preserve.Contains(Path.GetFileName(file)))
                continue;

            try { File.Delete(file); }
            catch (Exception ex) { errors.Add($"LocalAppData/{Path.GetFileName(file)}: {ex.Message}"); }
        }

        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            await DeleteWithRetryAsync(
                dir,
                $"LocalAppData/{Path.GetFileName(dir)}",
                errors,
                cancellationToken);
        }
    }
}
