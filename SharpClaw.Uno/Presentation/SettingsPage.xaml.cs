using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;

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

    public SettingsPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Cursor.SetCommand("sharpclaw settings ");
        await FetchCurrentUserInfoAsync();
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
        AddTabSection("Agents");
        AddTabButton("Agents", "sharpclaw agent list");
        AddTabButton("Roles", "sharpclaw role list");
        AddTabSection("Audio");
        AddTabButton("Sound Input", "sharpclaw resource audiodevice list");
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
        HighlightTabs();
        ContentPanel.Children.Clear();
        _ = tab switch
        {
            "Providers" => LoadProvidersAsync(),
            "Models" => LoadModelsAsync(),
            "Agents" => LoadAgentsAsync(),
            "Roles" => LoadRolesListAsync(),
            "Sound Input" => LoadSoundInputAsync(),
            "Users" => LoadUsersAsync(),
            "Danger Zone" => LoadDangerZoneAsync(),
            _ => Task.CompletedTask,
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
                var resp = await Api.PostAsync("/resources/audiodevices/sync", null);
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

        var devices = await FetchListAsync<AudioDeviceEntry>("/resources/audiodevices");
        if (devices is not { Count: > 0 })
        {
            Lbl("No audio devices found. Click sync to detect system devices.", 0x808080);
            return;
        }

        Sub("Select Input Device");
        var deviceBox = new ComboBox { FontFamily = Mono, FontSize = 11, Background = B(0x1A1A1A), Foreground = B(0xCCCCCC),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1), MinWidth = 300 };
        deviceBox.Items.Add(new ComboBoxItem { Content = "(none)", Tag = Guid.Empty });
        var savedId = LoadLocalSetting(ClientSettings.SelectedAudioDeviceId);
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
                SaveLocalSetting(ClientSettings.SelectedAudioDeviceId, id == Guid.Empty ? null : id.ToString());
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

        Sub("Global Permissions");
        Lbl("Capabilities the agent can use. Each has its own clearance level.", 0x808080);
        if (!_permEditor.BuildGlobalFlags(ContentPanel))
            Lbl("You hold no global permissions to grant.", 0x555555);

        Sub("Resource Accesses");
        Lbl("Per-resource grants with individual clearance levels.", 0x808080);
        await _permEditor.BuildResourceGrantsAsync(ContentPanel);

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
            Status(resp.IsSuccessStatusCode ? "✓ Permissions saved." : "✗ Save failed.", resp.IsSuccessStatusCode ? 0x00FF00 : 0xFF4444);
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
            PlaceholderText = searchHint, FontFamily = Mono, FontSize = 11,
            Foreground = B(0xCCCCCC), Background = B(0x1A1A1A),
            BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4), Margin = new Thickness(0, 2, 0, 4),
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
    private sealed record AgentEntry(Guid Id, string Name, string? SystemPrompt, Guid ModelId, string ModelName, string ProviderName, Guid? RoleId = null, string? RoleName = null, Dictionary<string, JsonElement>? ProviderParameters = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record RoleEntry(Guid Id, string Name, Guid? PermissionSetId = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record ResolvedFile(string DownloadUrl, string Filename, string? Quantization);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record AudioDeviceEntry(Guid Id, string Name, string? DeviceIdentifier, string? Description);
    [ImplicitKeys(IsEnabled = false)]
    private sealed record UserListEntry(Guid Id, string Username, string? Bio, Guid? RoleId, string? RoleName, bool IsUserAdmin);

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
            Text = "Reset All Data", FontFamily = Mono, FontSize = 13,
            Foreground = B(0xFF4444), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Permanently deletes the entire local database, all saved settings, "
                 + "encryption keys, API keys, chat history, and the first-setup marker. "
                 + "The application will restart as if freshly installed.",
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
            Content = new TextBlock { Text = "[ Reset All Data ]", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF4444) },
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
        Lbl("Stopping backend and deleting all local data…", 0xFF8888);

        await Task.Delay(200); // Let UI render

        var errors = new List<string>();

        // 1. Stop the backend process so files are not locked.
        try
        {
            var backend = App.Services?.GetService<BackendProcessManager>();
            backend?.Stop();
        }
        catch (Exception ex) { errors.Add($"Stop backend: {ex.Message}"); }

        // Small delay to let the process release file handles.
        await Task.Delay(500);

        // 2. Clear frontend-only preferences (client-settings.json) in memory
        //    so they are not re-flushed to disk before the directory is deleted.
        try { App.Services?.GetService<ClientSettings>()?.Reset(); }
        catch (Exception ex) { errors.Add($"Client settings: {ex.Message}"); }

        // 2b. Clear saved account store.
        try { App.Services?.GetService<AccountStore>()?.Reset(); }
        catch (Exception ex) { errors.Add($"Account store: {ex.Message}"); }

        // 3. Delete %LOCALAPPDATA%/SharpClaw (api-key, encryption keys, setup marker,
        //    client-settings.json).
        try
        {
            var localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SharpClaw");
            if (Directory.Exists(localAppData))
                Directory.Delete(localAppData, recursive: true);
        }
        catch (Exception ex) { errors.Add($"LocalAppData: {ex.Message}"); }

        // 4. Delete the backend's Data directory (JSON persistence).
        //    It lives next to the backend executable or, in dev mode, next
        //    to the Infrastructure assembly.  We check both the bundled
        //    location and the common dev-time location.
        try
        {
            var baseDir = AppContext.BaseDirectory;
            DeleteIfExists(Path.Combine(baseDir, "backend", "Data"));
            DeleteIfExists(Path.Combine(baseDir, "Data"));
        }
        catch (Exception ex) { errors.Add($"Data dir: {ex.Message}"); }

        // 5. Delete the backend's Environment directory (config files).
        try
        {
            var baseDir = AppContext.BaseDirectory;
            DeleteIfExists(Path.Combine(baseDir, "backend", "Environment"));
        }
        catch (Exception ex) { errors.Add($"Backend env: {ex.Message}"); }

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

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
