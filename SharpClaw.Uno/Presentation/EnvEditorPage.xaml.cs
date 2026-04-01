using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;
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
    private bool _jsonViewActive;

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
            EnvTarget.Core => "Application Core",
            EnvTarget.Gateway => "Public Gateway",
            _ => "Application Interface",
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
            LoadEntriesFromFile();
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
                ShowStatus("✗ Access denied — admin login required to edit Application Core.", error: true);
                return;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ShowStatus("✗ Core .env file not found on the server.", error: true);
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
    private void LoadEntriesFromFile()
    {
        _entries.Clear();
        EntriesPanel.Children.Clear();

        if (!File.Exists(_envFilePath))
        {
            ShowStatus("✗ .env file not found at path above.", error: true);
            return;
        }

        try
        {
            PopulateEntries(File.ReadAllText(_envFilePath));
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Failed to load: {ex.Message}", error: true);
        }
    }

    private void PopulateEntries(string raw)
    {
        if (!TryParseStructuredJson(raw))
            ParseEnvLines(raw.Split('\n'));

        foreach (var entry in _entries)
            EntriesPanel.Children.Add(BuildEntryRow(entry));

        ShowStatus($"✓ Loaded {_entries.Count} setting(s).", error: false, success: true);
    }

    private void ParseEnvLines(string[] lines)
    {
        var lastComment = string.Empty;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // Skip structural braces
            if (trimmed is "{" or "}" or "") continue;

            // Pure comment line (section header / description)
            if (trimmed.StartsWith("//") && !trimmed.Contains('"'))
            {
                lastComment = trimmed.TrimStart('/').Trim();
                continue;
            }

            // Commented-out JSON key line: //"Key": { "Sub": "Value" }
            if (trimmed.StartsWith("//") && trimmed.Contains('"'))
            {
                var uncommented = trimmed.TrimStart('/').Trim();
                if (TryParseJsonLine(uncommented, out var key, out var value))
                {
                    _entries.Add(new EnvEntry(key, value, lastComment, isActive: false));
                    lastComment = string.Empty;
                }
                continue;
            }

            // Active JSON key line: "Key": { "Sub": "Value" }
            if (trimmed.StartsWith('"'))
            {
                var clean = trimmed.TrimEnd(',');
                if (TryParseJsonLine(clean, out var key, out var value))
                {
                    _entries.Add(new EnvEntry(key, value, lastComment, isActive: true));
                    lastComment = string.Empty;
                }
            }
        }
    }

    private bool TryParseStructuredJson(string json)
    {
        try
        {
            var stripped = StripJsonComments(json);
            using var doc = JsonDocument.Parse(stripped,
                new JsonDocumentOptions { AllowTrailingCommas = true });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string value;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    foreach (var sub in prop.Value.EnumerateObject())
                        parts.Add($"{sub.Name}={sub.Value}");
                    value = string.Join(", ", parts);
                }
                else
                {
                    value = prop.Value.ToString();
                }
                _entries.Add(new EnvEntry(prop.Name, value, string.Empty, isActive: true));
            }

            return _entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string StripJsonComments(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (var line in json.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("//"))
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static bool TryParseJsonLine(string line, out string key, out string value)
    {
        key = value = string.Empty;

        // Expected format: "SectionKey": { "SubKey": "Val", ... }
        // or "SectionKey": "simple-value"
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return false;

        key = line[..colonIdx].Trim().Trim('"');
        var rawValue = line[(colonIdx + 1)..].Trim().TrimEnd(',');

        // Nested object: flatten sub-keys into a display string
        if (rawValue.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                var parts = new List<string>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    parts.Add($"{prop.Name}={prop.Value}");
                value = string.Join(", ", parts);
                return true;
            }
            catch
            {
                value = rawValue;
                return true;
            }
        }

        value = rawValue.Trim('"');
        return true;
    }

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
            var json = _jsonViewActive ? JsonTextBox.Text : BuildEnvJson();

            if (_target == EnvTarget.Core)
            {
                // Server-side write — the API enforces auth.
                if (!await SaveCoreViaApiAsync(json))
                    return;
            }
            else
            {
                var dir = Path.GetDirectoryName(_envFilePath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(_envFilePath, json);
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
            // If in JSON view, re-parse entries from the editor first
            if (_jsonViewActive)
                SyncEntriesFromJson();

            // For Core target, persist through the API so the server
            // re-validates auth before any disk write.
            if (_target == EnvTarget.Core)
            {
                var json = BuildEnvJson();
                if (!await SaveCoreViaApiAsync(json))
                    return;
            }

            foreach (var entry in _entries)
            {
                if (!entry.IsActive) continue;

                // Parse the flattened "SubKey=val, SubKey2=val2" back into env vars
                // as "Key:SubKey" = "val" (IConfiguration section format)
                foreach (var part in entry.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIdx = part.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var subKey = part[..eqIdx].Trim().Trim('"');
                        var val = part[(eqIdx + 1)..].Trim().Trim('"');
                        Environment.SetEnvironmentVariable($"{entry.Key}__{subKey}", val);
                    }
                    else
                    {
                        // Simple value
                        Environment.SetEnvironmentVariable(entry.Key, entry.Value.Trim('"'));
                        break;
                    }
                }
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
            ShowStatus("✓ Saved. Gateway is not currently running (enable it in Application Interface to auto-start).", error: false, success: true);
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

    private string BuildEnvJson()
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        foreach (var entry in _entries)
        {
            if (!entry.IsActive)
            {
                // Write as a comment-hint: we can't write JSON comments, so
                // inactive entries are simply omitted from the output.
                // They'll appear as commented lines when we write raw text instead.
                continue;
            }

            // Try to reconstruct the nested object from "SubKey=val, ..."
            var parts = entry.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Contains('='))
            {
                writer.WritePropertyName(entry.Key);
                writer.WriteStartObject();
                foreach (var part in parts)
                {
                    var eqIdx = part.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var subKey = part[..eqIdx].Trim().Trim('"');
                        var val = part[(eqIdx + 1)..].Trim().Trim('"');
                        writer.WriteString(subKey, val);
                    }
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString(entry.Key, entry.Value.Trim('"'));
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── View mode toggle ──────────────────────────────────────────

    private async void OnViewToggleClick(object sender, RoutedEventArgs e)
    {
        _jsonViewActive = !_jsonViewActive;

        if (_jsonViewActive)
        {
            // Switching to JSON view — show panel immediately, load content
            EntriesScroller.Visibility = Visibility.Collapsed;
            JsonPanel.Visibility = Visibility.Visible;
            CopyJsonButton.Visibility = Visibility.Visible;
            PasteJsonButton.Visibility = Visibility.Visible;
            ViewToggleLabel.Text = "☰ Options";
            ViewToggleLabel.Foreground = TerminalUI.Brush(0xFF9944);
            await SyncJsonFromFileAsync();
        }
        else
        {
            // Switching back to entries view — re-parse from the JSON TextBox
            SyncEntriesFromJson();
            JsonPanel.Visibility = Visibility.Collapsed;
            EntriesScroller.Visibility = Visibility.Visible;
            CopyJsonButton.Visibility = Visibility.Collapsed;
            PasteJsonButton.Visibility = Visibility.Collapsed;
            ViewToggleLabel.Text = "{ } JSON";
            ViewToggleLabel.Foreground = TerminalUI.Brush(0x66CCFF);
        }
    }

    private async Task SyncJsonFromFileAsync()
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
                        JsonTextBox.Text = doc.RootElement.GetProperty("content").GetString()!;
                        return;
                    }
                }
            }
            catch { /* fall through */ }
        }
        else if (File.Exists(_envFilePath))
        {
            try
            {
                JsonTextBox.Text = File.ReadAllText(_envFilePath);
                return;
            }
            catch { /* fall through to entries-based generation */ }
        }

        // Fallback: generate from current entries
        JsonTextBox.Text = BuildEnvJson();
    }

    private void SyncEntriesFromJson()
    {
        var json = JsonTextBox.Text;
        if (string.IsNullOrWhiteSpace(json)) return;

        _entries.Clear();
        EntriesPanel.Children.Clear();

        if (!TryParseStructuredJson(json))
            ParseEnvLines(json.Split('\n'));

        foreach (var entry in _entries)
            EntriesPanel.Children.Add(BuildEntryRow(entry));

        ShowStatus($"✓ Refreshed {_entries.Count} setting(s) from JSON.", error: false, success: true);
    }

    // ── Copy / Paste (Upload) ──────────────────────────────────────

    private void OnCopyJsonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(JsonTextBox.Text);
            Clipboard.SetContent(dp);
            ShowStatus("✓ Copied to clipboard.", error: false, success: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"✗ Copy failed: {ex.Message}", error: true);
        }
    }

    private async void OnPasteJsonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    JsonTextBox.Text = text;
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
                ShowStatus("✗ Access denied — admin login required to edit Application Core.", error: true);
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
