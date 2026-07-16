using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;
using SharpClaw.Configuration;
using Supprocom.Secrets;
using Windows.ApplicationModel.DataTransfer;

namespace SharpClaw.Presentation;

public sealed partial class EnvEditorPage : Page
{
    /// <summary>Set by the caller before navigation.</summary>
    public static EnvTarget PendingTarget { get; set; }

    private static FontFamily Mono => TerminalUI.Mono;

    private EnvTarget _target;
    private string _envFilePath = string.Empty;
    private readonly List<EnvEntry> _entries = [];
    private bool _rawViewActive;

    public EnvEditorPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _target = PendingTarget;

        TitleBlock.Text = _target switch
        {
            EnvTarget.Core => "Runtime Host",
            EnvTarget.Gateway => "Public Gateway",
            _ => "Client Interface",
        };

        if (_target == EnvTarget.Core)
        {
            PathBlock.Visibility = Visibility.Collapsed;
            await LoadEntriesFromApiAsync();
        }
        else
        {
            // Interface and Gateway targets: local file I/O.
            _envFilePath = _target == EnvTarget.Gateway
                ? ResolveGatewayEnvFilePath()
                : ResolveInterfaceEnvFilePath();
            PathBlock.Text = _envFilePath;
            await LoadEntriesFromFileAsync();
        }
    }

    // ── Path resolution ────────────────────────────────────────────

    private static string ResolveInterfaceEnvFilePath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(EnvEditorPage).Assembly.Location)!,
            "Environment", ".env");
    }

    private static string ResolveGatewayEnvFilePath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(EnvEditorPage).Assembly.Location)!,
            "gateway", "Environment", ".env");
    }

    // ── Load / Parse ───────────────────────────────────────────────

    /// <summary>
    /// Loads the Core .env content via <c>GET /env/core</c>.
    /// The API enforces auth — a 403 means the user is not allowed.
    /// </summary>
    private async Task LoadEntriesFromApiAsync()
    {
        _entries.Clear();
        EntriesPanel.Children.Clear();

        if (App.Services is not { } services) return;

        try
        {
            var api = services.GetRequiredService<SharpClawApiClient>();
            using var resp = await api.GetAsync("/env/core");

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                ShowStatus("✗ Access denied — admin login required to edit Runtime Host.", error: true);
                return;
            }

            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var content = doc.RootElement.GetProperty("content").GetString()!;

            PopulateEntries(content);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Failed to load: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Loads the Interface .env content from local disk (client's own file).
    /// </summary>
    private async Task LoadEntriesFromFileAsync()
    {
        _entries.Clear();
        EntriesPanel.Children.Clear();

        try
        {
            PopulateEntries(await ReadLocalDocumentAsync());
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Failed to load: {ex.Message}", error: true);
        }
    }

    private void PopulateEntries(string raw)
    {
        _entries.AddRange(ParseStructuredDocument(raw)
            .Select(setting => new EnvEntry(
                setting.Key,
                setting.Value,
                string.Empty,
                isActive: true)));

        foreach (var entry in _entries)
            EntriesPanel.Children.Add(BuildEntryRow(entry));

        ShowStatus($"✓ Loaded {_entries.Count} setting(s).", error: false, success: true);
    }

    internal static IReadOnlyList<SupprocomSecretSetting> ParseStructuredDocument(
        string document) =>
        SupprocomSecretDocument.Parse(document).Settings;

    internal static string SerializeStructuredDocument(
        IEnumerable<SupprocomSecretSetting> settings) =>
        SupprocomSecretDocument.Serialize(settings);

    private SupprocomSecretFileStore CreateLocalSecretStore() =>
        CreateLocalSecretStore(Path.GetDirectoryName(_envFilePath)!);

    internal static SupprocomSecretFileStore CreateLocalSecretStore(
        string envDirectory,
        string? installationKeyPath = null) =>
        new(LocalEnvironment.CreateSecretsOptions(
            envDirectory,
            isDevelopment: false,
            installationKeyPath: installationKeyPath));

    private Task<string> ReadLocalDocumentAsync() =>
        ReadLocalDocumentAsync(Path.GetDirectoryName(_envFilePath)!);

    internal static Task<string> ReadLocalDocumentAsync(
        string envDirectory,
        string? installationKeyPath = null) =>
        CreateLocalSecretStore(envDirectory, installationKeyPath).ReadDocumentAsync();

    internal static Task ReplaceLocalDocumentAsync(
        string envDirectory,
        string document,
        string? installationKeyPath = null) =>
        CreateLocalSecretStore(envDirectory, installationKeyPath).ReplaceDocumentAsync(document);

    private Task SaveLocalDocumentAsync(string document) =>
        CreateLocalSecretStore().ReplaceDocumentAsync(document);

    // ── UI builders ────────────────────────────────────────────────

    private Border BuildEntryRow(EnvEntry entry)
    {
        var container = new Border
        {
            Background = TerminalUI.Brush(entry.IsActive ? 0x0D1A0D : 0x1A1A1A),
            BorderBrush = TerminalUI.Brush(entry.IsActive ? 0x1A331A : 0x2A2A2A),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10),
        };

        var sp = new StackPanel { Spacing = 6 };

        // Header row: toggle + key name
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var toggle = new ToggleSwitch
        {
            IsOn = entry.IsActive,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        toggle.Toggled += (_, _) => entry.IsActive = toggle.IsOn;
        headerRow.Children.Add(toggle);

        headerRow.Children.Add(new TextBlock
        {
            Text = entry.Key,
            FontFamily = Mono,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TerminalUI.Brush(entry.IsActive ? 0x00FF00 : 0x808080),
            VerticalAlignment = VerticalAlignment.Center,
        });

        sp.Children.Add(headerRow);

        // Description
        if (!string.IsNullOrEmpty(entry.Description))
        {
            sp.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontFamily = Mono,
                FontSize = 11,
                Foreground = TerminalUI.Brush(0x666666),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Value editor
        var valueBox = new TextBox
        {
            Text = entry.Value,
            FontFamily = Mono,
            FontSize = 12,
            Foreground = TerminalUI.Brush(0xCCCCCC),
            Background = TerminalUI.Brush(0x0D0D0D),
            BorderBrush = TerminalUI.Brush(0x333333),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        valueBox.TextChanged += (_, _) => entry.Value = valueBox.Text;
        sp.Children.Add(valueBox);

        container.Child = sp;
        return container;
    }

    // ── Save / Run once ────────────────────────────────────────────

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = _rawViewActive ? RawTextBox.Text : BuildEnvDocument();

            if (_target == EnvTarget.Core)
            {
                // Server-side write — the API enforces auth.
                if (!await SaveCoreViaApiAsync(content))
                    return;
            }
            else
            {
                await SaveLocalDocumentAsync(content);
            }

            ShowStatus("> Saved. Restarting service...", error: false);
            await RestartBackendAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Save failed: {ex.Message}", error: true);
        }
    }

    private async void OnRunOnceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // If in raw mode, re-parse the package document before applying it.
            if (_rawViewActive && !SyncEntriesFromRaw())
                return;

            // For Core target, persist through the API so the server
            // re-validates auth before any disk write.
            if (_target == EnvTarget.Core)
            {
                var content = BuildEnvDocument();
                if (!await SaveCoreViaApiAsync(content))
                    return;
            }

            foreach (var entry in _entries.Where(entry => entry.IsActive))
            {
                var environmentName = entry.Key.Replace(":", "__", StringComparison.Ordinal);
                Environment.SetEnvironmentVariable(environmentName, entry.Value);
            }
            ShowStatus("> Applied. Restarting service...", error: false);
            await RestartBackendAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Failed: {ex.Message}", error: true);
        }
    }

    private async Task RestartBackendAsync()
    {
        if (App.Services is not { } services) return;

        if (_target == EnvTarget.Gateway)
        {
            await RestartGatewayAsync(services);
            return;
        }

        var backend = services.GetRequiredService<BackendProcessManager>();
        var apiClient = services.GetRequiredService<SharpClawApiClient>();

        if (backend.IsExternal)
        {
            // Dev mode — we don't own the process; just tell the user.
            ShowStatus("✓ Applied. The API is running externally — restart it manually to pick up changes.", error: false, success: true);
            return;
        }

        backend.Stop();
        apiClient.InvalidateApiKey();

        // Brief pause to let the process release the port.
        await Task.Delay(500);

        try
        {
            await backend.EnsureStartedAsync();

            // Wait for the API to become reachable.
            for (var i = 0; i < 20; i++)
            {
                if (await backend.IsApiReachableAsync())
                {
                    ShowStatus("✓ Service restarted successfully.", error: false, success: true);
                    return;
                }
                await Task.Delay(500);
            }

            ShowStatus("⚠ Service started but not yet reachable. It may still be initializing.", error: false, success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Restart failed: {ex.Message}", error: true);
        }
    }

    private async Task RestartGatewayAsync(IServiceProvider services)
    {
        var gateway = services.GetRequiredService<GatewayProcessManager>();

        if (gateway.IsExternal)
        {
            ShowStatus("✓ Applied. The gateway is running externally — restart it manually to pick up changes.", error: false, success: true);
            return;
        }

        if (gateway.SkipLaunch && !gateway.IsRunning)
        {
            ShowStatus("✓ Saved. Gateway is not currently running (enable it in Client Interface to auto-start).", error: false, success: true);
            return;
        }

        gateway.Stop();
        await Task.Delay(500);

        try
        {
            await gateway.EnsureStartedAsync();

            for (var i = 0; i < 20; i++)
            {
                if (await gateway.IsGatewayReachableAsync())
                {
                    ShowStatus("✓ Gateway restarted successfully.", error: false, success: true);
                    return;
                }
                await Task.Delay(500);
            }

            ShowStatus("⚠ Gateway started but not yet reachable. It may still be initializing.", error: false, success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Gateway restart failed: {ex.Message}", error: true);
        }
    }

    private string BuildEnvDocument() =>
        SerializeStructuredDocument(_entries
            .Where(entry => entry.IsActive)
            .Select(entry => new SupprocomSecretSetting(entry.Key, entry.Value)));

    // ── View mode toggle ──────────────────────────────────────────

    private async void OnViewToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_rawViewActive)
        {
            if (!await SyncRawFromFileAsync())
                return;

            _rawViewActive = true;
            // Switching to raw dotenv view — show panel immediately, load content
            EntriesScroller.Visibility = Visibility.Collapsed;
            RawPanel.Visibility = Visibility.Visible;
            CopyRawButton.Visibility = Visibility.Visible;
            PasteRawButton.Visibility = Visibility.Visible;
            ViewToggleLabel.Text = "☰ Options";
            ViewToggleLabel.Foreground = TerminalUI.Brush(0xFF9944);
        }
        else
        {
            // Switching back to entries view — re-parse the raw dotenv editor
            if (!SyncEntriesFromRaw())
                return;

            _rawViewActive = false;
            RawPanel.Visibility = Visibility.Collapsed;
            EntriesScroller.Visibility = Visibility.Visible;
            CopyRawButton.Visibility = Visibility.Collapsed;
            PasteRawButton.Visibility = Visibility.Collapsed;
            ViewToggleLabel.Text = "Raw dotenv";
            ViewToggleLabel.Foreground = TerminalUI.Brush(0x66CCFF);
        }
    }

    private async Task<bool> SyncRawFromFileAsync()
    {
        if (_target == EnvTarget.Core)
        {
            // Fetch latest from the API.
            try
            {
                if (App.Services is { } services)
                {
                    var api = services.GetRequiredService<SharpClawApiClient>();
                    using var resp = await api.GetAsync("/env/core");
                    if (resp.IsSuccessStatusCode)
                    {
                        using var stream = await resp.Content.ReadAsStreamAsync();
                        using var doc = await JsonDocument.ParseAsync(stream);
                        RawTextBox.Text = doc.RootElement.GetProperty("content").GetString()!;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"✗ Failed to load raw dotenv: {ex.Message}", error: true);
                return false;
            }

            ShowStatus("✗ Runtime Host did not return an env document.", error: true);
            return false;
        }
        else
        {
            try
            {
                RawTextBox.Text = await ReadLocalDocumentAsync();
                return true;
            }
            catch (Exception ex)
            {
                ShowStatus($"✗ Failed to load raw dotenv: {ex.Message}", error: true);
                return false;
            }
        }
    }

    private bool SyncEntriesFromRaw()
    {
        var raw = RawTextBox.Text;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        _entries.Clear();
        EntriesPanel.Children.Clear();

        IReadOnlyList<SupprocomSecretSetting> settings;
        try
        {
            settings = ParseStructuredDocument(raw);
        }
        catch (Exception ex)
        {
            ShowStatus($"Invalid dotenv: {ex.Message}", error: true);
            return false;
        }

        _entries.AddRange(settings.Select(setting => new EnvEntry(
            setting.Key,
            setting.Value,
            string.Empty,
            isActive: true)));

        foreach (var entry in _entries)
            EntriesPanel.Children.Add(BuildEntryRow(entry));

        ShowStatus($"✓ Refreshed {_entries.Count} setting(s) from dotenv.", error: false, success: true);
        return true;
    }

    // ── Copy / Paste (Upload) ──────────────────────────────────────

    private void OnCopyRawClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(RawTextBox.Text);
            Clipboard.SetContent(dp);
            ShowStatus("✓ Copied to clipboard.", error: false, success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Copy failed: {ex.Message}", error: true);
        }
    }

    private async void OnPasteRawClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    RawTextBox.Text = text;
                    ShowStatus("✓ Pasted from clipboard. Review and save when ready.", error: false, success: true);
                    return;
                }
            }
            ShowStatus("✗ Clipboard is empty or does not contain text.", error: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Upload failed: {ex.Message}", error: true);
        }
    }

    // ── Core API helpers ─────────────────────────────────────────

    /// <summary>
    /// Writes Core .env content via <c>PUT /env/core</c>.
    /// Returns <c>false</c> and shows an error when the server rejects
    /// the request (auth failure, etc.).
    /// </summary>
    private async Task<bool> SaveCoreViaApiAsync(string content)
    {
        if (App.Services is not { } services)
            return false;

        try
        {
            var api = services.GetRequiredService<SharpClawApiClient>();
            var payload = JsonSerializer.Serialize(new { content });
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await api.PutAsync("/env/core", body);

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                ShowStatus("✗ Access denied — admin login required to edit Runtime Host.", error: true);
                return false;
            }

            resp.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Save failed: {ex.Message}", error: true);
            return false;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "EnvMenu");
    }

    private void ShowStatus(string text, bool error, bool success = false)
    {
        StatusBlock.Text = text;
        StatusBlock.Foreground = TerminalUI.Brush(
            error ? 0xFF4444 : success ? 0x32CD32 : 0x808080);
        StatusBlock.Visibility = Visibility.Visible;
    }

    private sealed class EnvEntry(string key, string value, string description, bool isActive)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
        public string Description { get; } = description;
        public bool IsActive { get; set; } = isActive;
    }
}
