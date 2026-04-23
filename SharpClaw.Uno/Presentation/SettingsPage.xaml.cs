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

    private string _activeTab = "Providers";

    // Role editor state
    private PermissionEditorBuilder? _permEditor;

    // Cached lists for cross-tab use
    private List<ProviderEntry>? _cachedProviders;
    private List<RoleEntry>? _cachedRoles;
    private List<ModelEntry>? _cachedModels;

    // Current user info for permission filtering & admin tab
    private JsonElement? _callerPermissions;
    private bool _isUserAdmin;
    private Guid? _callerRoleId;

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
        _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
        BuildTabs();
        SelectTab("Providers");
    }

    // ═══════════════════════════════════════════════════════════════
    // Sidebar tabs
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps existing sidebar tabs to the module that must be enabled for them to appear.
    /// Tabs not in this dictionary are always visible.
    /// </summary>
    private static readonly Dictionary<string, string> TabRequiredModules = new()
    {
        ["Containers"]          = "sharpclaw_mk8shell",
        ["Websites"]            = "sharpclaw_web_access",
        ["Search Engines"]      = "sharpclaw_web_access",
        ["Internal Databases"]  = "sharpclaw_database_access",
        ["External Databases"]  = "sharpclaw_database_access",
        ["Sound Input"]         = "sharpclaw_transcription",
        ["Display Devices"]     = "sharpclaw_computer_use",
        ["Native Applications"] = "sharpclaw_computer_use",
        ["Editor Sessions"]     = "sharpclaw_editor_common",
        ["Documents"]           = "sharpclaw_office_apps",
        ["Bot Integrations"]    = "sharpclaw_bot_integration",
    };

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        AddTabSection("Models");
        AddTabButton("Providers", "sharpclaw provider list");
        AddTabButton("Models", "sharpclaw model list");
        AddTabSection("Agents");
        AddTabButton("Agents", "sharpclaw agent list");
        AddTabButton("Roles", "sharpclaw role list");
        AddTabSection("Resources");
        AddConditionalTabButton("Containers", "sharpclaw resource container list");
        AddConditionalTabButton("Websites", "sharpclaw resource website list");
        AddConditionalTabButton("Search Engines", "sharpclaw resource searchengine list");
        AddConditionalTabButton("Internal Databases", "sharpclaw resource internaldatabase list");
        AddConditionalTabButton("External Databases", "sharpclaw resource externaldatabase list");
        AddConditionalTabButton("Sound Input", "sharpclaw resource inputaudio list");
        AddConditionalTabButton("Display Devices", "sharpclaw resource displaydevice list");
        AddConditionalTabButton("Native Applications", "sharpclaw resource nativeapp list");
        AddConditionalTabButton("Editor Sessions", "sharpclaw resource editorsession list");
        AddConditionalTabButton("Documents", "sharpclaw resource document list");
        AddTabSection("Gateway");
        AddTabButton("Gateway", "sharpclaw gateway status");
        AddConditionalTabButton("Bot Integrations", "sharpclaw bot list");
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

    /// <summary>Only adds the tab button if its required module is enabled.</summary>
    private void AddConditionalTabButton(string label, string cursorCmd)
    {
        if (TabRequiredModules.TryGetValue(label, out var requiredModule))
        {
            var moduleCache = App.Services?.GetService<ModuleStateCache>();
            if (moduleCache is not null && !moduleCache.IsEnabled(requiredModule))
                return;
            if (moduleCache is null
                && _cachedModuleStates?.Any(m => m.ModuleId == requiredModule && m.Enabled) != true)
                return;
        }
        AddTabButton(label, cursorCmd);
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
        // Guard: if a selected tab requires a disabled module, redirect.
        if (TabRequiredModules.TryGetValue(tab, out var req))
        {
            var moduleCache = App.Services?.GetService<ModuleStateCache>();
            var enabled = moduleCache is not null
                ? moduleCache.IsEnabled(req)
                : _cachedModuleStates?.Any(m => m.ModuleId == req && m.Enabled) == true;
            if (!enabled)
            {
                SelectTab("Manage Modules");
                return;
            }
        }

        _activeTab = tab;
        StopGatewayLogTimer();
        StopModuleLogTimer();
        HighlightTabs();
        ContentPanel.Children.Clear();
        _ = tab switch
        {
            "Providers" => LoadProvidersAsync(),
            "Models" => LoadModelsAsync(),
            "Agents" => LoadAgentsAsync(),
            "Roles" => LoadRolesListAsync(),
            "Containers" => LoadGenericResourceAsync("Containers", "Sandboxed environments for shell execution.", "/resources/containers", hasSync: true),
            "Websites" => LoadGenericResourceAsync("Websites", "Allowed websites for web browsing tools.", "/resources/websites"),
            "Search Engines" => LoadGenericResourceAsync("Search Engines", "Search engines available for web search tools.", "/searchengines"),
            "Internal Databases" => LoadGenericResourceAsync("Internal Databases", "Local information stores for knowledge retrieval.", "/resources/internaldatabases"),
            "External Databases" => LoadGenericResourceAsync("External Databases", "External database connections for data access.", "/resources/externaldatabases"),
            "Sound Input" => LoadSoundInputAsync(),
            "Display Devices" => LoadGenericResourceAsync("Display Devices", "Display devices for screen capture and interaction.", "/resources/displaydevices", hasSync: true),
            "Native Applications" => LoadGenericResourceAsync("Native Applications", "Registered native applications for automation.", "/resources/nativeapplications"),
            "Editor Sessions" => LoadGenericResourceAsync("Editor Sessions", "Active editor bridge connections.", "/resources/editorsessions"),
            "Documents" => LoadGenericResourceAsync("Documents", "Document sessions for office automation.", "/resources/documents"),
            "Gateway" => LoadGatewayAsync(),
            "Bot Integrations" => LoadBotIntegrationsAsync(),
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

        var nameBox = MakeInput("Provider name…");
        var typeBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 200 };
        foreach (var t in TerminalUI.ProviderTypeNames)
            typeBox.Items.Add(new ComboBoxItem { Content = t, Tag = t });
        typeBox.SelectedIndex = 0;
        var epBox = MakeInput("https://... (Custom only)");
        epBox.Visibility = Visibility.Collapsed;
        typeBox.SelectionChanged += (_, _) =>
            epBox.Visibility = typeBox.SelectedItem is ComboBoxItem { Tag: "Custom" } ? Visibility.Visible : Visibility.Collapsed;
        var createBtn = GreenButton("Create");
        createBtn.Click += async (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var type = typeBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "OpenAI";
            if (string.IsNullOrEmpty(name)) return;
            var ep = type == "Custom" ? epBox.Text.Trim() : null;
            var body = JsonSerializer.Serialize(new { name, providerType = type, apiEndpoint = ep }, Json);
            await Api.PostAsync("/providers", new StringContent(body, Encoding.UTF8, "application/json"));
            await LoadProvidersAsync();
        };
        form.Children.Add(nameBox);
        form.Children.Add(typeBox);
        form.Children.Add(epBox);
        form.Children.Add(createBtn);

        if (_cachedProviders is { Count: > 0 })
            foreach (var p in _cachedProviders)
                listPanel.Children.Add(MakeListRow(p.Name, p.ProviderType,
                    () => ShowProviderDetail(p),
                    async () => { await Api.DeleteAsync($"/providers/{p.Id}"); await LoadProvidersAsync(); },
                    p.HasApiKey ? "✓ key" : "✗ no key", p.HasApiKey ? 0x00FF00 : 0xFF4444));
    }

    private void ShowProviderDetail(ProviderEntry p)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadProvidersAsync());
        H($"Provider: {p.Name}");
        Lbl($"type: {p.ProviderType}   key: {(p.HasApiKey ? "✓ set" : "✗ not set")}", 0x999999);
        Lbl($"id: {p.Id}", 0x555555);

        var isDeviceCode = TerminalUI.DeviceCodeProviderTypes.Contains(p.ProviderType);

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
                provBox.Items.Add(new ComboBoxItem { Content = $"{prov.Name} ({prov.ProviderType})", Tag = prov.Id });
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
    // AGENTS
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadAgentsAsync()
    {
        ContentPanel.Children.Clear();
        var (form, listPanel) = TabHeader("Agents", "Search agents…");
        var agents = await FetchListAsync<AgentEntry>("/agents");
        _cachedModels ??= await FetchListAsync<ModelEntry>("/models");
        _cachedRoles ??= await FetchListAsync<RoleEntry>("/roles");

        var nameBox = MakeInput("Agent name…");
        var modelBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 280 };
        if (_cachedModels is { Count: > 0 })
        {
            foreach (var m in _cachedModels)
                modelBox.Items.Add(new ComboBoxItem { Content = $"{m.Name} ({m.ProviderName})", Tag = m.Id });
            modelBox.SelectedIndex = 0;
        }
        var promptBox = new TextBox { FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00),
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            PlaceholderText = "System prompt (optional)…", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var createBtn = GreenButton("Create");
        createBtn.Click += async (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name) || modelBox.SelectedItem is not ComboBoxItem { Tag: Guid modelId }) return;
            var prompt = string.IsNullOrWhiteSpace(promptBox.Text) ? null : promptBox.Text.Trim();
            var body = JsonSerializer.Serialize(new { name, modelId, systemPrompt = prompt }, Json);
            await Api.PostAsync("/agents", new StringContent(body, Encoding.UTF8, "application/json"));
            await LoadAgentsAsync();
        };
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = "model:", FontFamily = Mono, FontSize = 11, Foreground = B(0xCCCCCC) });
        form.Children.Add(modelBox);
        form.Children.Add(new TextBlock { Text = "system prompt:", FontFamily = Mono, FontSize = 11, Foreground = B(0xCCCCCC) });
        form.Children.Add(promptBox);
        form.Children.Add(createBtn);

        var syncBtn = GreenButton("↻ Sync agents from models");
        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false;
            try
            {
                var resp = await Api.PostAsync("/agents/sync-with-models", null);
                Status(resp.IsSuccessStatusCode ? "✓ Agents synced." : "✗ Sync failed.",
                    resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
                if (resp.IsSuccessStatusCode) await LoadAgentsAsync();
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { syncBtn.IsEnabled = true; }
        };
        form.Children.Add(syncBtn);

        if (agents is { Count: > 0 })
            foreach (var a in agents)
                listPanel.Children.Add(MakeListRow(a.Name,
                    $"{a.ModelName} ({a.ProviderName}){(a.RoleName is not null ? $"  role: {a.RoleName}" : "")}",
                    () => ShowAgentDetail(a),
                    async () => { await Api.DeleteAsync($"/agents/{a.Id}"); await LoadAgentsAsync(); }));
    }

    private void ShowAgentDetail(AgentEntry a)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadAgentsAsync());
        H($"Agent: {a.Name}");
        Lbl($"model: {a.ModelName} ({a.ProviderName})", 0x999999);
        Lbl($"id: {a.Id}", 0x555555);

        // Edit system prompt
        Sub("System Prompt");
        var promptBox = new TextBox { FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00),
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Text = a.SystemPrompt ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        var savePromptBtn = GreenButton("Save Prompt");
        savePromptBtn.Click += async (_, _) =>
        {
            var prompt = string.IsNullOrWhiteSpace(promptBox.Text) ? null : promptBox.Text.Trim();
            var body = JsonSerializer.Serialize(new { systemPrompt = prompt }, Json);
            var resp = await Api.PutAsync($"/agents/{a.Id}",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Status(resp.IsSuccessStatusCode ? "✓ Prompt saved." : "✗ Save failed.",
                resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
        };
        ContentPanel.Children.Add(promptBox);
        ContentPanel.Children.Add(savePromptBtn);

        // Provider parameters
        Sub("Provider Parameters");
        Lbl("Optional JSON key-value pairs merged into every API request for this agent's provider "
            + "(e.g. {\"response_mime_type\":\"application/json\"} for Gemini).", 0x808080);
        var currentParams = a.ProviderParameters is { Count: > 0 }
            ? JsonSerializer.Serialize(a.ProviderParameters, new JsonSerializerOptions { WriteIndented = true })
            : "{}";
        var paramsBox = new TextBox { FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00),
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Text = currentParams, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var saveParamsBtn = GreenButton("Save Parameters");
        saveParamsBtn.Click += async (_, _) =>
        {
            Dictionary<string, JsonElement>? parsed = null;
            var raw = paramsBox.Text?.Trim();
            if (!string.IsNullOrEmpty(raw) && raw != "{}")
            {
                try
                {
                    parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
                }
                catch (JsonException)
                {
                    Status("\u2717 Invalid JSON.", 0xFF4444);
                    return;
                }
            }
            var body = JsonSerializer.Serialize(new { providerParameters = parsed }, Json);
            var resp = await Api.PutAsync($"/agents/{a.Id}",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Status(resp.IsSuccessStatusCode ? "\u2713 Parameters saved." : "\u2717 Save failed.",
                resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
        };
        ContentPanel.Children.Add(paramsBox);
        ContentPanel.Children.Add(saveParamsBtn);

        // Custom chat header
        Sub("Custom Chat Header");
        Lbl("Template override for the metadata header prepended to each message. "
            + "Use {{tag}} placeholders (e.g. {{time}}, {{user}}, {{agent-role}}, {{Agents:{Name} ({Id})}}).", 0x808080);
        var headerBox = new TextBox { FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00),
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Text = a.CustomChatHeader ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 80,
            PlaceholderText = "(uses default header)" };
        var saveHeaderBtn = GreenButton("Save Header");
        saveHeaderBtn.Click += async (_, _) =>
        {
            var header = string.IsNullOrWhiteSpace(headerBox.Text) ? null : headerBox.Text.Trim();
            var body = JsonSerializer.Serialize(new { customChatHeader = header }, Json);
            var resp = await Api.PutAsync($"/agents/{a.Id}",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Status(resp.IsSuccessStatusCode ? "\u2713 Header saved." : "\u2717 Save failed.",
                resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
        };
        ContentPanel.Children.Add(headerBox);
        ContentPanel.Children.Add(saveHeaderBtn);

        // Assign role
        Sub("Role");
        _cachedRoles ??= [];
        var roleBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 240 };
        roleBox.Items.Add(new ComboBoxItem { Content = "(none — remove role)", Tag = Guid.Empty });
        var selIdx = 0;
        if (_cachedRoles.Count > 0)
        {
            for (var i = 0; i < _cachedRoles.Count; i++)
            {
                roleBox.Items.Add(new ComboBoxItem { Content = _cachedRoles[i].Name, Tag = _cachedRoles[i].Id });
                if (_cachedRoles[i].Id == a.RoleId) selIdx = i + 1;
            }
        }
        roleBox.SelectedIndex = selIdx;
        var assignBtn = GreenButton("Assign Role");
        assignBtn.Click += async (_, _) =>
        {
            if (roleBox.SelectedItem is not ComboBoxItem { Tag: Guid roleId }) return;
            var body = JsonSerializer.Serialize(new { roleId }, Json);
            var resp = await Api.PutAsync($"/agents/{a.Id}/role",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Status(resp.IsSuccessStatusCode ? "✓ Role assigned." : "✗ Failed.",
                resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
        };
        ContentPanel.Children.Add(roleBox);
        ContentPanel.Children.Add(assignBtn);
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERIC RESOURCE LIST
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadGenericResourceAsync(string title, string description, string apiPath, bool hasSync = false)
    {
        ContentPanel.Children.Clear();
        H(title);
        Lbl(description, 0x808080);

        if (hasSync)
        {
            var syncBtn = GreenButton("↻ Sync");
            syncBtn.Click += async (_, _) =>
            {
                syncBtn.IsEnabled = false;
                try
                {
                    var resp = await Api.PostAsync($"{apiPath}/sync", null);
                    if (resp.IsSuccessStatusCode)
                    {
                        Status("✓ Synced.", 0x00FF00);
                        await LoadGenericResourceAsync(title, description, apiPath, hasSync);
                    }
                    else Status("✗ Sync failed.", 0xFF4444);
                }
                catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
                finally { syncBtn.IsEnabled = true; }
            };
            ContentPanel.Children.Add(syncBtn);
        }

        try
        {
            using var resp = await Api.GetAsync(apiPath);
            if (!resp.IsSuccessStatusCode)
            {
                Status($"Failed to load: {(int)resp.StatusCode}", 0xFF4444);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var items = doc.RootElement;

            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                Lbl("No items found.", 0x808080);
                return;
            }

            Sub($"{items.GetArrayLength()} item(s)");
            var list = new StackPanel { Spacing = 2 };

            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;
                var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;

                var idShort = id is { Length: >= 8 } ? id[..8] + "…" : id;

                var capturedApiPath = apiPath;
                var capturedId = id;
                var capturedTitle = title;
                var capturedDesc = description;
                var capturedSync = hasSync;

                Func<Task>? onDelete = capturedId is not null
                    ? async () =>
                    {
                        try
                        {
                            var delResp = await Api.DeleteAsync($"{capturedApiPath}/{capturedId}");
                            if (delResp.IsSuccessStatusCode)
                                await LoadGenericResourceAsync(capturedTitle, capturedDesc, capturedApiPath, capturedSync);
                            else
                                Status($"✗ Delete failed: {(int)delResp.StatusCode}", 0xFF4444);
                        }
                        catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
                    }
                    : null;

                list.Children.Add(MakeListRow(name ?? "(unnamed)", idShort, onClick: null, onDelete));
            }

            ContentPanel.Children.Add(list);
        }
        catch (Exception ex)
        {
            Status($"✗ {ex.Message}", 0xFF4444);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SOUND INPUT
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadSoundInputAsync()
    {
        ContentPanel.Children.Clear();
        H("Sound Input");
        Lbl("Audio input device used for transcription.", 0x808080);

        var syncBtn = GreenButton("↻ Sync audio devices");
        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false;
            try
            {
                var resp = await Api.PostAsync("/resources/inputaudios/sync", null);
                if (resp.IsSuccessStatusCode)
                {
                    using var s = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(s);
                    var imported = doc.RootElement.GetProperty("imported").GetInt32();
                    var skipped = doc.RootElement.GetProperty("skipped").GetInt32();
                    Status($"✓ Synced: {imported} imported, {skipped} skipped.", 0x00FF00);
                    await LoadSoundInputAsync();
                }
                else Status("✗ Sync failed.", 0xFF4444);
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { syncBtn.IsEnabled = true; }
        };
        ContentPanel.Children.Add(syncBtn);

        var devices = await FetchListAsync<InputAudioEntry>("/resources/inputaudios");
        if (devices is not { Count: > 0 })
        {
            Lbl("No audio devices found. Click sync to detect system devices.", 0x808080);
            return;
        }

        Sub("Select Input Device");
        var deviceBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 300 };
        deviceBox.Items.Add(new ComboBoxItem { Content = "(none)", Tag = Guid.Empty });
        var savedId = LoadLocalSetting(ClientSettings.SelectedInputAudioId);
        var selIdx = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var label = d.DeviceIdentifier is not null ? $"{d.Name}  [{d.DeviceIdentifier}]" : d.Name;
            deviceBox.Items.Add(new ComboBoxItem { Content = label, Tag = d.Id });
            if (savedId is not null && Guid.TryParse(savedId, out var sid) && sid == d.Id)
                selIdx = i + 1;
        }
        deviceBox.SelectedIndex = selIdx;
        deviceBox.SelectionChanged += (_, _) =>
        {
            if (deviceBox.SelectedItem is ComboBoxItem { Tag: Guid id })
            {
                SaveLocalSetting(ClientSettings.SelectedInputAudioId, id == Guid.Empty ? null : id.ToString());
                Status(id == Guid.Empty ? "Input device cleared." : "✓ Input device saved.", id == Guid.Empty ? 0x808080 : 0x00FF00);
            }
        };
        ContentPanel.Children.Add(deviceBox);

        Sub("Detected Devices");
        var list = new StackPanel { Spacing = 2 };
        foreach (var d in devices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = "›", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) });
            row.Children.Add(new TextBlock { Text = d.Name, FontFamily = Mono, FontSize = 12, Foreground = B(0xE0E0E0) });
            if (d.DeviceIdentifier is not null)
                row.Children.Add(new TextBlock { Text = d.DeviceIdentifier, FontFamily = Mono, FontSize = 10, Foreground = B(0x555555), VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(row);
        }
        ContentPanel.Children.Add(list);
    }

    private static void SaveLocalSetting(string key, string? value)
        => App.Services?.GetService<ClientSettings>()?.Set(key, value);

    private static string? LoadLocalSetting(string key)
        => App.Services?.GetService<ClientSettings>()?.Get(key);

    // ═══════════════════════════════════════════════════════════════
    // GATEWAY
    // ═══════════════════════════════════════════════════════════════

    private GatewayProcessManager? Gateway => App.Services?.GetService<GatewayProcessManager>();

    /// <summary>Creates a raw <see cref="HttpClient"/> pointed at the gateway.</summary>
    private HttpClient CreateGatewayClient()
    {
        var gw = Gateway ?? throw new InvalidOperationException("GatewayProcessManager not registered.");
        return new HttpClient { BaseAddress = new Uri(gw.ClientUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

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
        var reachable = await gw.IsGatewayReachableAsync();
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

        clearBtn.Click += (_, _) =>
        {
            gw.ClearOutput();
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
                gw.ApiKey = Api.CachedApiKey;
                await gw.EnsureStartedAsync();
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
                    if (await gw.IsGatewayReachableAsync())
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

        stopBtn.Click += (_, _) =>
        {
            gw.Stop();
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
                gw.Stop();
                await Task.Delay(500);
                gw.ApiKey = Api.CachedApiKey;
                gw.Start();

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
                    if (await gw.IsGatewayReachableAsync())
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
            var online = await gw.IsGatewayReachableAsync();
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

        persistentToggle.Toggled += (_, _) =>
        {
            var on = persistentToggle.IsOn;
            if (backend is not null) backend.Persistent = on;
            if (gw is not null) gw.Persistent = on;

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

            autoStartToggle.Toggled += (_, _) =>
            {
                var on = autoStartToggle.IsOn;

                if (backend is not null)
                    WindowsStartupManager.SetBackendAutoStart(on && !backend.SkipLaunch, backend.ExecutablePath, backend.ApiUrl);

                if (gw is not null)
                    WindowsStartupManager.SetGatewayAutoStart(on && !gw.SkipLaunch, gw.ExecutablePath, gw.GatewayUrl);

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
    // BOT INTEGRATIONS
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadBotIntegrationsAsync()
    {
        ContentPanel.Children.Clear();

        var gw = Gateway;
        if (gw is null)
        {
            H("Bot Integrations");
            Status("GatewayProcessManager is not registered.", 0xFF4444);
            return;
        }

        var reachable = await gw.IsGatewayReachableAsync();
        if (!reachable)
        {
            H("Bot Integrations");
            Status("Gateway is not running. Start the gateway from the Gateway tab to manage bot integrations.", 0xFF8800);
            return;
        }

        var (form, listPanel) = TabHeader("Bot Integrations", "Search bots…");

        // ── Create form ──
        var nameBox = MakeInput("Bot name…");
        var typeBox = new ComboBox
        {
            FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 200,
        };
        foreach (var t in new[] { "Telegram", "Discord", "WhatsApp", "Slack", "Matrix", "Signal", "Email", "Teams" })
            typeBox.Items.Add(new ComboBoxItem { Content = t, Tag = t });
        typeBox.SelectedIndex = 0;

        var tokenBox = new PasswordBox
        {
            PlaceholderText = "Bot API token…",
            FontFamily = Mono, FontSize = 12,
            Foreground = B(0x00FF00), Background = B(0x1A1A1A),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            MinWidth = 260, Padding = new Thickness(8, 6),
        };

        // ── Platform-specific config fields (shown dynamically) ──
        var platformConfigPanel = new StackPanel { Spacing = 4 };
        var platformInputs = new Dictionary<string, TextBox>();

        void RebuildPlatformFields(string type)
        {
            platformConfigPanel.Children.Clear();
            platformInputs.Clear();
            var fields = GetPlatformConfigFields(type);
            if (fields.Length == 0) return;
            platformConfigPanel.Children.Add(new TextBlock
            {
                Text = "platform config:", FontFamily = Mono, FontSize = 11, Foreground = B(0xCCCCCC),
            });
            foreach (var (key, placeholder, hint) in fields)
            {
                if (hint is not null)
                    platformConfigPanel.Children.Add(new TextBlock
                    {
                        Text = hint, FontFamily = Mono, FontSize = 10, Foreground = B(0x666666),
                        TextWrapping = TextWrapping.Wrap,
                    });
                var input = MakeInput(placeholder);
                platformInputs[key] = input;
                platformConfigPanel.Children.Add(input);
            }
        }

        typeBox.SelectionChanged += (_, _) =>
        {
            var sel = typeBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "Telegram";
            tokenBox.PlaceholderText = GetTokenPlaceholder(sel);
            RebuildPlatformFields(sel);
        };
        RebuildPlatformFields("Telegram");

        var enabledToggle = new ToggleSwitch
        {
            IsOn = true,
            OnContent = new TextBlock { Text = "Enabled", FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00) },
            OffContent = new TextBlock { Text = "Disabled", FontFamily = Mono, FontSize = 11, Foreground = B(0x666666) },
        };

        var createBtn = GreenButton("Create");
        createBtn.Click += async (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var type = typeBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "Telegram";
            if (string.IsNullOrEmpty(name)) return;
            var token = string.IsNullOrEmpty(tokenBox.Password) ? null : tokenBox.Password;
            var payload = new JsonObject { ["name"] = name, ["botType"] = type, ["enabled"] = enabledToggle.IsOn };
            if (token is not null) payload["botToken"] = token;
            var pc = BuildPlatformConfigJson(platformInputs);
            if (pc is not null) payload["platformConfig"] = pc;
            try
            {
                using var gwHttp = CreateGatewayClient();
                using var resp = await gwHttp.PostAsync("/api/bots",
                    new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    Status($"✗ Create failed: {body}", 0xFF4444);
                    return;
                }
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); return; }
            await LoadBotIntegrationsAsync();
        };

        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = "type:", FontFamily = Mono, FontSize = 11, Foreground = B(0xCCCCCC) });
        form.Children.Add(typeBox);
        form.Children.Add(new TextBlock { Text = "token:", FontFamily = Mono, FontSize = 11, Foreground = B(0xCCCCCC) });
        form.Children.Add(tokenBox);
        form.Children.Add(platformConfigPanel);
        form.Children.Add(enabledToggle);
        form.Children.Add(createBtn);

        // ── Fetch list ──
        List<BotIntegrationEntry>? bots;
        try
        {
            using var gwHttp = CreateGatewayClient();
            using var listResp = await gwHttp.GetAsync("/api/bots/list");
            if (!listResp.IsSuccessStatusCode)
            {
                var code = (int)listResp.StatusCode;
                var detail = code switch
                {
                    502 => "Core API is not reachable — the gateway cannot connect to the internal API.",
                    401 => "Authentication failed — try restarting both the backend and gateway.",
                    503 => "The bots endpoint is disabled in the gateway configuration.",
                    _ => $"Gateway returned HTTP {code}.",
                };
                Status($"✗ {detail}", 0xFF4444);
                return;
            }
            var json = await listResp.Content.ReadAsStringAsync();
            bots = JsonSerializer.Deserialize<List<BotIntegrationEntry>>(json, Json);
        }
        catch (Exception ex)
        {
            Status($"✗ {ex.Message}", 0xFF4444);
            return;
        }

        if (bots is { Count: > 0 })
            foreach (var bot in bots)
            {
                var statusText = bot.Enabled ? "● enabled" : "○ disabled";
                var statusClr = bot.Enabled ? 0x00FF00 : 0x666666;
                listPanel.Children.Add(MakeListRow(
                    bot.Name,
                    bot.BotType + (bot.HasBotToken ? "  🔑" : ""),
                    () => ShowBotDetail(bot),
                    async () =>
                    {
                        try
                        {
                            using var gwHttp = CreateGatewayClient();
                            await gwHttp.DeleteAsync($"/api/bots/{bot.Id}");
                        }
                        catch { /* best-effort */ }
                        await LoadBotIntegrationsAsync();
                    },
                    statusText, statusClr));
            }
    }

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

    private void ShowBotDetail(BotIntegrationEntry bot)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadBotIntegrationsAsync());
        H($"Bot: {bot.Name}");
        Lbl($"type: {bot.BotType}   status: {(bot.Enabled ? "enabled" : "disabled")}   token: {(bot.HasBotToken ? "✓ set" : "✗ not set")}", 0x999999);
        Lbl($"id: {bot.Id}", 0x555555);

        // ── Edit name ──
        Sub("Name");
        var nameBox = MakeInput("Bot name…");
        nameBox.Text = bot.Name;
        ContentPanel.Children.Add(nameBox);

        // ── Toggle enabled ──
        Sub("Enabled");
        var toggle = new ToggleSwitch
        {
            IsOn = bot.Enabled,
            OnContent = new TextBlock { Text = "Enabled", FontFamily = Mono, FontSize = 11, Foreground = B(0x00FF00) },
            OffContent = new TextBlock { Text = "Disabled", FontFamily = Mono, FontSize = 11, Foreground = B(0x666666) },
        };
        ContentPanel.Children.Add(toggle);

        // ── Bot token ──
        Sub("Bot Token");
        var tokenHint = GetTokenHint(bot.BotType);
        Lbl(tokenHint, 0x666666);
        var tokenBox = new PasswordBox
        {
            PlaceholderText = bot.HasBotToken ? "(token set — enter new value to replace)" : GetTokenPlaceholder(bot.BotType),
            FontFamily = Mono, FontSize = 12,
            Foreground = B(0x00FF00), Background = B(0x1A1A1A),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            MinWidth = 360, Padding = new Thickness(8, 6),
        };
        ContentPanel.Children.Add(tokenBox);

        // ── Platform-specific config fields ──
        var platformInputs = new Dictionary<string, TextBox>();
        var fields = GetPlatformConfigFields(bot.BotType);
        Dictionary<string, string>? existingConfig = null;
        if (bot.PlatformConfig is not null)
        {
            try { existingConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(bot.PlatformConfig, Json); }
            catch { /* ignore malformed */ }
        }

        if (fields.Length > 0)
        {
            Sub("Platform Configuration");
            foreach (var (key, placeholder, hint) in fields)
            {
                if (hint is not null)
                    Lbl(hint, 0x666666);
                var input = MakeInput(placeholder);
                if (existingConfig is not null && existingConfig.TryGetValue(key, out var val))
                    input.Text = val;
                platformInputs[key] = input;
                ContentPanel.Children.Add(input);
            }
        }

        // ── Save ──
        var saveBtn = GreenButton("Save");
        saveBtn.Click += async (_, _) =>
        {
            saveBtn.IsEnabled = false;
            try
            {
                var payload = new JsonObject
                {
                    ["name"] = nameBox.Text.Trim(),
                    ["enabled"] = toggle.IsOn,
                };
                var newToken = tokenBox.Password;
                if (!string.IsNullOrEmpty(newToken))
                    payload["botToken"] = newToken;
                var pc = BuildPlatformConfigJson(platformInputs);
                if (pc is not null)
                    payload["platformConfig"] = pc;

                using var gwHttp = CreateGatewayClient();
                using var resp = await gwHttp.PutAsync($"/api/bots/{bot.Id}",
                    new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    Status("✓ Saved. Restart the gateway for changes to take effect.", 0x00FF00);
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    Status($"✗ Save failed: {body}", 0xFF4444);
                }
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { saveBtn.IsEnabled = true; }
        };
        ContentPanel.Children.Add(saveBtn);
    }

    // ── Bot platform config helpers ─────────────────────────────

    /// <summary>
    /// Returns the platform-specific config fields needed for each bot type.
    /// Platforms that only require a bot token return an empty array.
    /// </summary>
    private static (string Key, string Placeholder, string? Hint)[] GetPlatformConfigFields(string type) => type switch
    {
        "WhatsApp" =>
        [
            ("PhoneNumberId", "Phone Number ID…", "WhatsApp Business phone number ID from Meta Cloud API."),
            ("VerifyToken", "Webhook verify token…", "Arbitrary string used for Meta webhook verification."),
        ],
        "Slack" =>
        [
            ("SigningSecret", "Slack signing secret…", "App signing secret from the Slack API dashboard, used to verify webhook requests."),
        ],
        "Matrix" =>
        [
            ("HomeserverUrl", "https://matrix.org", "Matrix homeserver base URL (e.g. https://matrix.org)."),
        ],
        "Signal" =>
        [
            ("ApiUrl", "http://localhost:8080", "signal-cli REST API base URL."),
            ("PhoneNumber", "+1234567890", "Registered phone number in E.164 format."),
        ],
        "Email" =>
        [
            ("ImapHost", "imap.example.com", "IMAP server hostname for receiving email."),
            ("ImapPort", "993", "IMAP port (default 993 for TLS)."),
            ("SmtpHost", "smtp.example.com", "SMTP server hostname for sending replies."),
            ("SmtpPort", "587", "SMTP port (default 587 for STARTTLS)."),
            ("Username", "bot@example.com", "Email account username / address."),
            ("PollIntervalSeconds", "30", "IMAP poll interval in seconds."),
        ],
        "Teams" =>
        [
            ("AppId", "00000000-0000-0000-0000-000000000000", "Microsoft App ID (GUID) from Azure Bot registration."),
        ],
        _ => [], // Telegram, Discord — token only
    };

    private static string GetTokenPlaceholder(string type) => type switch
    {
        "Telegram" => "Bot token from @BotFather…",
        "Discord" => "Bot token from Developer Portal…",
        "WhatsApp" => "Meta Graph API access token…",
        "Slack" => "Bot User OAuth Token (xoxb-…)…",
        "Matrix" => "Matrix access token…",
        "Signal" => "(unused — signal-cli handles auth)",
        "Email" => "Email password / app password…",
        "Teams" => "Azure AD client secret…",
        _ => "Bot API token…",
    };

    private static string GetTokenHint(string type) => type switch
    {
        "Telegram" => "Bot token from @BotFather on Telegram.",
        "Discord" => "Bot token from the Discord Developer Portal.",
        "WhatsApp" => "Meta Graph API access token (permanent system user token recommended).",
        "Slack" => "Bot User OAuth Token (xoxb-…) from the Slack API dashboard.",
        "Matrix" => "Matrix access token — obtain via /_matrix/client/v3/login.",
        "Signal" => "Not used — signal-cli handles authentication via the registered phone number.",
        "Email" => "Email account password or app-specific password.",
        "Teams" => "Azure AD client secret from the Azure Bot registration.",
        _ => "Bot token for this integration.",
    };

    /// <summary>
    /// Serialises the platform-specific input fields into a JSON string
    /// suitable for the <c>platformConfig</c> API field. Returns
    /// <see langword="null"/> when no fields have values.
    /// </summary>
    private static string? BuildPlatformConfigJson(Dictionary<string, TextBox> inputs)
    {
        if (inputs.Count == 0) return null;
        var obj = new JsonObject();
        var hasValues = false;
        foreach (var (key, box) in inputs)
        {
            var val = box.Text.Trim();
            if (string.IsNullOrEmpty(val)) continue;
            obj[key] = val;
            hasValues = true;
        }
        return hasValues ? obj.ToJsonString() : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // ROLES
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadRolesListAsync()
    {
        ContentPanel.Children.Clear();
        var (form, listPanel) = TabHeader("Roles", "Search roles…");
        _cachedRoles = await FetchListAsync<RoleEntry>("/roles");

        var nameBox = MakeInput("New role name…");
        var createBtn = GreenButton("Create");
        createBtn.Click += async (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var body = JsonSerializer.Serialize(new { name }, Json);
            await Api.PostAsync("/roles", new StringContent(body, Encoding.UTF8, "application/json"));
            await LoadRolesListAsync();
        };
        form.Children.Add(nameBox);
        form.Children.Add(createBtn);

        if (_cachedRoles is { Count: > 0 })
            foreach (var role in _cachedRoles)
            {
                var r = role;
                var row = MakeListRow(r.Name, r.Id.ToString()[..8] + "…",
                    () => _ = LoadRoleDetailAsync(r.Id, r.Name),
                    async () => { await Api.DeleteAsync($"/roles/{r.Id}"); await LoadRolesListAsync(); });
                var clone = new Button
                {
                    Content = new TextBlock { Text = "⎘", FontFamily = Mono, FontSize = 12, Foreground = B(0x00CCFF) },
                    Background = Trans, BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(clone, "Duplicate role");
                clone.Click += async (_, _) =>
                {
                    try
                    {
                        var body = JsonSerializer.Serialize(new { name = $"Duplicate of {r.Name}" }, Json);
                        using var createResp = await Api.PostAsync("/roles", new StringContent(body, Encoding.UTF8, "application/json"));
                        if (!createResp.IsSuccessStatusCode) { Status("✗ Clone failed.", 0xFF4444); return; }
                        using var cs = await createResp.Content.ReadAsStreamAsync();
                        using var cd = await JsonDocument.ParseAsync(cs);
                        var newId = cd.RootElement.GetProperty("id").GetGuid();
                        using var permsResp = await Api.GetAsync($"/roles/{r.Id}/permissions");
                        if (permsResp.IsSuccessStatusCode)
                        {
                            var permsBody = await permsResp.Content.ReadAsStringAsync();
                            await Api.PutAsync($"/roles/{newId}/permissions",
                                new StringContent(permsBody, Encoding.UTF8, "application/json"));
                        }
                        await LoadRolesListAsync();
                    }
                    catch { Status("✗ Clone failed.", 0xFF4444); }
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(clone, row.ColumnDefinitions.Count - 1);
                row.Children.Add(clone);
                listPanel.Children.Add(row);
            }
    }

    private async Task LoadRoleDetailAsync(Guid roleId, string roleName)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadRolesListAsync());
        H($"Role: {roleName}");
        Lbl($"id: {roleId}", 0x555555);

        JsonElement? root = null;
        try
        {
            using var resp = await Api.GetAsync($"/roles/{roleId}/permissions");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                root = doc.RootElement.Clone();
            }
        }
        catch { /* swallow */ }

        if (root is null) { Status("✗ Failed to load permissions.", 0xFF4444); return; }
        await BuildRoleEditorAsync(roleId, root.Value);
    }

    private async Task BuildRoleEditorAsync(Guid roleId, JsonElement root)
    {
        _permEditor = new PermissionEditorBuilder(Api)
            .WithCallerFilter(_callerPermissions)
            .WithExisting(root)
            .WithFlagClearance(true)
            .WithGrantClearance(true);

        var metadata = await TerminalUI.LoadPermissionMetadataAsync(Api);

        Sub("Permissions (grouped by module)");
        Lbl("Capabilities and resource grants the agent can use, organised by owning module.", 0x808080);
        await _permEditor.BuildGroupedByModuleAsync(ContentPanel, metadata);

        var saveBtn = new Button
        {
            Content = new TextBlock { Text = "Save Permissions", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) },
            Background = B(0x1A2A1A), BorderBrush = B(0x00FF00), BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8), Margin = new Thickness(0, 12, 0, 0),
        };
        saveBtn.Click += async (_, _) => await SaveRolePermsAsync(roleId);
        ContentPanel.Children.Add(saveBtn);
    }

    private async Task SaveRolePermsAsync(Guid roleId)
    {
        if (_permEditor is null) return;
        try
        {
            var body = JsonSerializer.Serialize(_permEditor.CollectAll(), Json);
            var resp = await Api.PutAsync($"/roles/{roleId}/permissions",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) { Status("✓ Permissions saved.", 0x00FF00); return; }
            string errorMsg;
            try
            {
                var raw = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(raw);
                errorMsg = doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? "Save failed."
                    : "Save failed.";
            }
            catch { errorMsg = "Save failed."; }
            Status($"✗ {errorMsg}", 0xFF4444);
        }
        catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
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

    private static ComboBox ClearanceCombo(string selected, bool includeUnset)
        => TerminalUI.MakeClearanceCombo(selected, includeUnset);

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

    private static string FormatFlagName(string camelCase) => TerminalUI.FormatFlagName(camelCase);

    private static SolidColorBrush B(int rgb) => TerminalUI.Brush(rgb);

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Main");
    }

    private void OnEnvClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        EnvMenuPage.PendingOrigin = "Settings";
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvMenu");
    }

    // ═══════════════════════════════════════════════════════════════
    // Current user info & permission helpers
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
                var root = doc.RootElement;
                _isUserAdmin = root.TryGetProperty("isUserAdmin", out var adminProp) && adminProp.GetBoolean();
                _callerRoleId = root.TryGetProperty("roleId", out var roleProp) && roleProp.ValueKind != JsonValueKind.Null
                    ? roleProp.GetGuid() : null;
            }
        }
        catch { /* swallow */ }

        if (_callerRoleId is { } roleId)
        {
            try
            {
                using var resp = await Api.GetAsync($"/roles/{roleId}/permissions");
                if (resp.IsSuccessStatusCode)
                {
                    using var s = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(s);
                    _callerPermissions = doc.RootElement.Clone();
                }
            }
            catch { /* swallow */ }
        }
    }

    /// <summary>
    /// Returns the set of resource IDs the caller holds for a given access type,
    /// or <c>null</c> if the caller has no grants of that type.
    /// </summary>
    private HashSet<Guid>? GetCallerResourceIds(string accessType)
    {
        if (_callerPermissions is not { } perms) return null;
        if (!perms.TryGetProperty(accessType, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var ids = new HashSet<Guid>();
        foreach (var g in arr.EnumerateArray())
            if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                ids.Add(rid.GetGuid());
        return ids.Count > 0 ? ids : null;
    }

    // ═══════════════════════════════════════════════════════════════
    // USERS (admin only)
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadUsersAsync()
    {
        ContentPanel.Children.Clear();
        H("Users");
        Lbl("Manage registered users and assign roles. Requires admin.", 0x808080);

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

        _cachedRoles ??= await FetchListAsync<RoleEntry>("/roles");

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
            if (u.RoleName is not null)
                headerRow.Children.Add(new TextBlock { Text = $"role: {u.RoleName}", FontFamily = Mono, FontSize = 10,
                    Foreground = B(0x808080), VerticalAlignment = VerticalAlignment.Center });
            userRow.Children.Add(headerRow);

            var roleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
            roleRow.Children.Add(new TextBlock { Text = "Role:", FontFamily = Mono, FontSize = 10,
                Foreground = B(0x808080), VerticalAlignment = VerticalAlignment.Center });
            var roleBox = new ComboBox { FontFamily = Mono, FontSize = 10, Background = B(0x1A1A1A),
                Foreground = B(0xCCCCCC), BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 200 };
            roleBox.Items.Add(new ComboBoxItem { Content = "(none)", Tag = Guid.Empty });
            var selIdx = 0;
            if (_cachedRoles is { Count: > 0 })
            {
                for (var i = 0; i < _cachedRoles.Count; i++)
                {
                    roleBox.Items.Add(new ComboBoxItem { Content = _cachedRoles[i].Name, Tag = _cachedRoles[i].Id });
                    if (_cachedRoles[i].Id == u.RoleId) selIdx = i + 1;
                }
            }
            roleBox.SelectedIndex = selIdx;

            var capturedUser = u;
            var assignBtn = new Button
            {
                Content = new TextBlock { Text = "Assign", FontFamily = Mono, FontSize = 10, Foreground = B(0x00FF00) },
                Background = B(0x1A2A1A), BorderBrush = B(0x00FF00), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3), MinWidth = 0, MinHeight = 0,
            };
            assignBtn.Click += async (_, _) =>
            {
                if (roleBox.SelectedItem is not ComboBoxItem { Tag: Guid roleId }) return;
                var body = JsonSerializer.Serialize(new { roleId }, Json);
                var resp = await Api.PutAsync($"/users/{capturedUser.Id}/role",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Status(resp.IsSuccessStatusCode
                    ? $"✓ Role updated for {capturedUser.Username}."
                    : "✗ Failed to assign role.",
                    resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
            };

            roleRow.Children.Add(roleBox);
            roleRow.Children.Add(assignBtn);
            userRow.Children.Add(roleRow);

            // Show user ID in muted text
            userRow.Children.Add(new TextBlock { Text = $"id: {u.Id}", FontFamily = Mono, FontSize = 9,
                Foreground = B(0x444444), Margin = new Thickness(16, 0, 0, 0) });

            list.Children.Add(userRow);
        }
        ContentPanel.Children.Add(list);
    }

    // ── DTOs ────────────────────────────────────────────────────

    [ImplicitKeys(IsEnabled = false)]
    private sealed record ProviderEntry(Guid Id, string Name, string ProviderType, bool HasApiKey, string? ApiEndpoint = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ModelEntry(Guid Id, string Name, Guid ProviderId, string ProviderName, string Capabilities);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record AgentEntry(Guid Id, string Name, string? SystemPrompt, Guid ModelId, string ModelName, string ProviderName, Guid? RoleId = null, string? RoleName = null, Dictionary<string, JsonElement>? ProviderParameters = null, string? CustomChatHeader = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record RoleEntry(Guid Id, string Name, Guid? PermissionSetId = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ResolvedFile(string DownloadUrl, string Filename, string? Quantization);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record InputAudioEntry(Guid Id, string Name, string? DeviceIdentifier, string? Description);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record UserListEntry(Guid Id, string Username, string? Bio, Guid? RoleId, string? RoleName, bool IsUserAdmin);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record BotIntegrationEntry(Guid Id, string Name, string BotType, bool Enabled, bool HasBotToken, Guid? DefaultChannelId = null, string? PlatformConfig = null);
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
            gateway?.Dispose();
        }
        catch { /* best-effort */ }

        try
        {
            var backend = App.Services?.GetService<BackendProcessManager>();
            backend?.Stop();
        }
        catch (Exception ex) { errors.Add($"Stop backend: {ex.Message}"); }

        // 3. Clear frontend-only preferences (client-settings.json) in memory
        //    so they are not re-flushed to disk before the directory is deleted.
        try { App.Services?.GetService<ClientSettings>()?.Reset(); }
        catch (Exception ex) { errors.Add($"Client settings: {ex.Message}"); }

        // 3b. Clear saved account store.
        try { App.Services?.GetService<AccountStore>()?.Reset(); }
        catch (Exception ex) { errors.Add($"Account store: {ex.Message}"); }

        // 4. Clean only the frontend-owned instance root. This also removes
        //    any bundled gateway companion state that lives under the current
        //    frontend stack. Backend-owned auth files and backend discovery
        //    entries are handled by backend reset/shutdown instead.
        try
        {
            var frontend = App.Services?.GetService<FrontendInstanceService>();
            if (frontend is not null)
                await DeleteWithRetryAsync(frontend.Paths.InstanceRoot, "Frontend instance", errors);
        }
        catch (Exception ex)
        {
            errors.Add($"Frontend instance: {ex.Message}");
        }

        // 5. Invalidate the client's cached API key so the next request
        //    re-reads from disk (handles both external and bundled restarts).
        try { App.Services?.GetService<SharpClawApiClient>()?.InvalidateApiKey(); }
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
            var navigator = services.GetRequiredService<INavigator>();
            await navigator.NavigateRouteAsync(this, "Boot", qualifier: Qualifiers.ClearBackStack);
        }
    }

    private static async Task DeleteWithRetryAsync(string path, string label, List<string> errors)
    {
        if (!Directory.Exists(path)) return;

        const int maxAttempts = 3;
        for (int i = 1; i <= maxAttempts; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (i < maxAttempts)
            {
                await Task.Delay(1000);
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
    private static async Task CleanDirectoryPreservingAuthFilesAsync(string path, List<string> errors)
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
            await DeleteWithRetryAsync(dir, $"LocalAppData/{Path.GetFileName(dir)}", errors);
        }
    }
}
