using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Module management tabs: manage list, per-module detail, log viewer, diagnostics.
public sealed partial class SettingsPage
{
    // ═══════════════════════════════════════════════════════════════
    // Module detail DTO (enriched response from GET /modules/{id})
    // ═══════════════════════════════════════════════════════════════

    [ImplicitKeys(IsEnabled = false)]
    private sealed record ModuleDetailEntry(
        string ModuleId, string DisplayName, string ToolPrefix,
        bool Enabled, string? Version, bool Registered, bool IsExternal,
        DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
        string? Author, string? Description, string? License,
        string[]? Platforms, int ExecutionTimeoutSeconds,
        int ToolCount, int InlineToolCount,
        string[] ExportedContracts, string[] RequiredContracts,
        bool AllRequirementsSatisfied);

    // ═══════════════════════════════════════════════════════════════
    // Module tab dispatch (called from SelectTab default case)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Handles clicks on per-module dynamic sidebar tabs.
    /// Matches by DisplayName → moduleId, then loads the detail page.
    /// </summary>
    private Task DispatchModuleTabAsync(string tabLabel)
    {
        var match = _cachedModuleStates?.FirstOrDefault(m => m.DisplayName == tabLabel);
        if (match is not null)
            return LoadModuleDetailAsync(match.ModuleId);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // MANAGE MODULES
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadManageModulesAsync()
    {
        ContentPanel.Children.Clear();
        H("Modules");
        Lbl("Manage installed and external modules.", 0x808080);

        // Scan button
        var scanBtn = GreenButton("↻ Scan for External Modules");
        scanBtn.Click += async (_, _) =>
        {
            scanBtn.IsEnabled = false;
            try
            {
                var resp = await Api.PostAsync("/modules/scan", null);
                if (resp.IsSuccessStatusCode)
                {
                    Status("✓ Scan complete.", 0x00FF00);
                    _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
                    BuildTabs();
                    await LoadManageModulesAsync();
                    return;
                }
                Status("✗ Scan failed.", 0xFF4444);
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { scanBtn.IsEnabled = true; }
        };
        ContentPanel.Children.Add(scanBtn);

        _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
        if (_cachedModuleStates is null or { Count: 0 })
        {
            Lbl("No modules found.", 0x808080);
            return;
        }

        var bundled = _cachedModuleStates.Where(m => !m.IsExternal).ToList();
        var external = _cachedModuleStates.Where(m => m.IsExternal).ToList();

        // ── Bundled ──
        Sub("── Bundled ──");
        var bundledList = new StackPanel { Spacing = 2 };
        foreach (var m in bundled)
            bundledList.Children.Add(BuildModuleRow(m));
        ContentPanel.Children.Add(bundledList);

        // ── External ──
        Sub("── External ──");
        if (external.Count == 0)
        {
            Lbl("(none loaded)", 0x555555);
        }
        else
        {
            var externalList = new StackPanel { Spacing = 2 };
            foreach (var m in external)
                externalList.Children.Add(BuildModuleRow(m, isExternal: true));
            ContentPanel.Children.Add(externalList);
        }
    }

    private Grid BuildModuleRow(ModuleStateEntry m, bool isExternal = false)
    {
        var row = new Grid { Tag = m.DisplayName };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Main button — navigate to detail
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Trans, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = "›", FontFamily = Mono, FontSize = 12, Foreground = B(0x00FF00) });
        sp.Children.Add(new TextBlock { Text = m.DisplayName, FontFamily = Mono, FontSize = 12, Foreground = B(0xE0E0E0) });
        if (m.Version is not null)
            sp.Children.Add(new TextBlock { Text = $"v{m.Version}", FontFamily = Mono, FontSize = 10,
                Foreground = B(0x555555), VerticalAlignment = VerticalAlignment.Center });

        // Status badge
        var (badgeText, badgeColor) = m.Enabled
            ? ("[ON]", 0x00FF00)
            : ("[OFF]", 0xFF4444);
        sp.Children.Add(new TextBlock { Text = badgeText, FontFamily = Mono, FontSize = 11,
            Foreground = B(badgeColor), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center });

        btn.Content = sp;
        btn.Click += (_, _) => _ = LoadModuleDetailAsync(m.ModuleId);
        Grid.SetColumn(btn, 0);
        row.Children.Add(btn);

        // Action buttons panel
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center };

        // Toggle button
        var toggleBtn = new Button
        {
            Content = new TextBlock
            {
                Text = m.Enabled ? "Disable" : "Enable", FontFamily = Mono, FontSize = 10,
                Foreground = B(m.Enabled ? 0xFF8800 : 0x00FF00),
            },
            Background = B(m.Enabled ? 0x2A1A0A : 0x1A2A1A),
            BorderBrush = B(m.Enabled ? 0xFF8800 : 0x00FF00),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3), MinWidth = 0, MinHeight = 0,
        };
        var capturedModule = m;
        toggleBtn.Click += async (_, _) =>
        {
            toggleBtn.IsEnabled = false;
            try
            {
                var endpoint = capturedModule.Enabled
                    ? $"/modules/{capturedModule.ModuleId}/disable"
                    : $"/modules/{capturedModule.ModuleId}/enable";
                var resp = await Api.PostAsync(endpoint, null);
                if (resp.IsSuccessStatusCode)
                {
                    _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
                    BuildTabs();
                    await LoadManageModulesAsync();
                    return;
                }
                // Show error from response
                var body = await resp.Content.ReadAsStringAsync();
                Status($"✗ {body}", 0xFF4444);
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { toggleBtn.IsEnabled = true; }
        };
        actions.Children.Add(toggleBtn);

        // External-only buttons
        if (isExternal)
        {
            var reloadBtn = new Button
            {
                Content = new TextBlock { Text = "↻", FontFamily = Mono, FontSize = 12, Foreground = B(0x00CCFF) },
                Background = Trans, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
            };
            ToolTipService.SetToolTip(reloadBtn, "Reload module");
            reloadBtn.Click += async (_, _) =>
            {
                reloadBtn.IsEnabled = false;
                try
                {
                    var resp = await Api.PostAsync($"/modules/{capturedModule.ModuleId}/reload", null);
                    if (resp.IsSuccessStatusCode)
                    {
                        _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
                        BuildTabs();
                        await LoadManageModulesAsync();
                        return;
                    }
                    Status("✗ Reload failed.", 0xFF4444);
                }
                catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
                finally { reloadBtn.IsEnabled = true; }
            };
            actions.Children.Add(reloadBtn);

            var unloadBtn = new Button
            {
                Content = new TextBlock { Text = "✗", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF4444) },
                Background = Trans, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
            };
            ToolTipService.SetToolTip(unloadBtn, "Unload module");
            unloadBtn.Click += async (_, _) =>
            {
                unloadBtn.IsEnabled = false;
                try
                {
                    var resp = await Api.PostAsync($"/modules/{capturedModule.ModuleId}/unload", null);
                    if (resp.IsSuccessStatusCode)
                    {
                        _cachedModuleStates = await FetchListAsync<ModuleStateEntry>("/modules");
                        BuildTabs();
                        await LoadManageModulesAsync();
                        return;
                    }
                    Status("✗ Unload failed.", 0xFF4444);
                }
                catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
                finally { unloadBtn.IsEnabled = true; }
            };
            actions.Children.Add(unloadBtn);
        }

        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    // ═══════════════════════════════════════════════════════════════
    // PER-MODULE DETAIL
    // ═══════════════════════════════════════════════════════════════

    private async Task LoadModuleDetailAsync(string moduleId)
    {
        ContentPanel.Children.Clear();
        BackLink(() => _ = LoadManageModulesAsync());

        ModuleDetailEntry? detail = null;
        try
        {
            using var resp = await Api.GetAsync($"/modules/{moduleId}");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                detail = await JsonSerializer.DeserializeAsync<ModuleDetailEntry>(s, Json);
            }
        }
        catch { /* swallow — handled below */ }

        if (detail is null)
        {
            Status($"✗ Module '{moduleId}' not found or API unreachable.", 0xFF4444);
            return;
        }

        // ── Header ──
        H($"Module: {detail.DisplayName}");
        Lbl($"id: {detail.ModuleId}", 0x555555);
        var meta = $"version: {detail.Version ?? "—"}   prefix: {detail.ToolPrefix}";
        if (detail.Platforms is { Length: > 0 })
            meta += $"   platforms: {string.Join(", ", detail.Platforms)}";
        Lbl(meta, 0x808080);
        Lbl($"status: {(detail.Enabled ? "● enabled" : "○ disabled")}",
            detail.Enabled ? 0x00FF00 : 0xFF4444);

        // ── Info section ──
        Sub("── Info ──");
        Lbl($"Tools: {detail.ToolCount} job-pipeline, {detail.InlineToolCount} inline", 0xCCCCCC);
        Lbl($"Exports: {(detail.ExportedContracts.Length > 0 ? string.Join(", ", detail.ExportedContracts) : "(none)")}", 0xCCCCCC);
        var reqText = detail.RequiredContracts.Length > 0
            ? string.Join(", ", detail.RequiredContracts)
            : "(none)";
        var reqSuffix = detail.RequiredContracts.Length > 0
            ? (detail.AllRequirementsSatisfied ? "  ✓ all satisfied" : "  ✗ unsatisfied")
            : "";
        Lbl($"Requires: {reqText}{reqSuffix}", 0xCCCCCC);
        if (detail.CreatedAt.HasValue)
            Lbl($"Created: {detail.CreatedAt.Value:yyyy-MM-dd HH:mm}", 0x555555);
        if (detail.UpdatedAt.HasValue)
            Lbl($"Updated: {detail.UpdatedAt.Value:yyyy-MM-dd HH:mm}", 0x555555);

        // ── Settings section (Phase 1 — read-only manifest fields) ──
        Sub("── Settings ──");
        Lbl($"Author: {detail.Author ?? "—"}", 0xCCCCCC);
        Lbl($"Description: {detail.Description ?? "—"}", 0xCCCCCC);
        Lbl($"License: {detail.License ?? "—"}", 0xCCCCCC);
        Lbl($"Execution timeout: {detail.ExecutionTimeoutSeconds}s", 0xCCCCCC);

        // ── Log Stream section ──
        BuildModuleLogSection(moduleId);

        // ── Errors section ──
        await BuildModuleDiagnosticsSection(moduleId, "error", "Errors", 0x331111);

        // ── Warnings section ──
        await BuildModuleDiagnosticsSection(moduleId, "warning", "Warnings", 0x332200);
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE LOG STREAM
    // ═══════════════════════════════════════════════════════════════

    private void BuildModuleLogSection(string moduleId)
    {
        Sub("── Log Stream ──");

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var startBtn = GreenButton("Start");
        var stopBtn = new Button
        {
            Content = new TextBlock { Text = "Stop", FontFamily = Mono, FontSize = 12, Foreground = B(0xFF8800) },
            Background = B(0x2A1A0A), BorderBrush = B(0xFF8800), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0), IsEnabled = false,
        };
        var clearBtn = new Button
        {
            Content = new TextBlock { Text = "Clear", FontFamily = Mono, FontSize = 12, Foreground = B(0x808080) },
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0),
        };
        btnRow.Children.Add(startBtn);
        btnRow.Children.Add(stopBtn);
        btnRow.Children.Add(clearBtn);
        ContentPanel.Children.Add(btnRow);

        var logScroll = new ScrollViewer
        {
            Background = B(0x0A0A0A),
            BorderBrush = B(0x333333),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            MinHeight = 140,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var logBlock = new TextBlock
        {
            FontFamily = Mono, FontSize = 10, Foreground = B(0x888888),
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Text = "(not started)",
        };
        logScroll.Content = logBlock;
        ContentPanel.Children.Add(logScroll);

        // Store references for timer
        _moduleLogBlock = logBlock;
        _moduleLogScroll = logScroll;
        _moduleLogCursor = null;
        _activeModuleLogId = moduleId;

        startBtn.Click += (_, _) =>
        {
            startBtn.IsEnabled = false;
            stopBtn.IsEnabled = true;
            _moduleLogCursor = null;
            _activeModuleLogId = moduleId;
            StartModuleLogTimer(moduleId);
        };

        stopBtn.Click += (_, _) =>
        {
            StopModuleLogTimer();
            startBtn.IsEnabled = true;
            stopBtn.IsEnabled = false;
        };

        clearBtn.Click += async (_, _) =>
        {
            clearBtn.IsEnabled = false;
            try
            {
                await Api.DeleteAsync($"/modules/{moduleId}/logs");
                logBlock.Text = "(cleared)";
                _moduleLogCursor = null;
            }
            catch (Exception ex) { Status($"✗ {ex.Message}", 0xFF4444); }
            finally { clearBtn.IsEnabled = true; }
        };
    }

    private void StartModuleLogTimer(string moduleId)
    {
        StopModuleLogTimer();
        _moduleLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _moduleLogTimer.Tick += async (_, _) => await PollModuleLogsAsync(moduleId);
        _moduleLogTimer.Start();
        // Fire immediately
        _ = PollModuleLogsAsync(moduleId);
    }

    private void StopModuleLogTimer()
    {
        _moduleLogTimer?.Stop();
        _moduleLogTimer = null;
    }

    private async Task PollModuleLogsAsync(string moduleId)
    {
        if (_moduleLogBlock is null || _moduleLogScroll is null) return;

        try
        {
            var url = $"/modules/{moduleId}/logs?take=100";
            if (_moduleLogCursor is not null)
                url += $"&since={Uri.EscapeDataString(_moduleLogCursor)}";

            using var resp = await Api.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                StopModuleLogTimer();
                _moduleLogBlock.Text += "\n[Connection lost]";
                _moduleLogBlock.Foreground = B(0xFF4444);
                return;
            }

            using var s = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(s);
            var root = doc.RootElement;

            if (root.TryGetProperty("cursor", out var cursorProp) && cursorProp.ValueKind == JsonValueKind.String)
                _moduleLogCursor = cursorProp.GetString();

            if (!root.TryGetProperty("entries", out var entries)) return;

            var newLines = new System.Text.StringBuilder();
            foreach (var entry in entries.EnumerateArray())
            {
                var ts = entry.TryGetProperty("timestamp", out var tsProp)
                    ? DateTimeOffset.Parse(tsProp.GetString()!).ToLocalTime().ToString("HH:mm:ss")
                    : "??:??:??";
                var lvl = entry.TryGetProperty("level", out var lvlProp)
                    ? $"[{lvlProp.GetString()![..3].ToUpperInvariant()}]"
                    : "[???]";
                var msg = entry.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? ""
                    : "";
                newLines.AppendLine($"{ts} {lvl} {msg}");
            }

            if (newLines.Length > 0)
            {
                var current = _moduleLogBlock.Text;
                if (current is "(not started)" or "(cleared)")
                    current = "";
                _moduleLogBlock.Text = current + newLines.ToString();
                _moduleLogBlock.Foreground = B(0x888888);
                _moduleLogScroll.UpdateLayout();
                _moduleLogScroll.ChangeView(null, _moduleLogScroll.ScrollableHeight, null, disableAnimation: true);
            }
        }
        catch
        {
            StopModuleLogTimer();
            if (_moduleLogBlock is not null)
            {
                _moduleLogBlock.Text += "\n[Connection lost]";
                _moduleLogBlock.Foreground = B(0xFF4444);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE DIAGNOSTICS (ERRORS / WARNINGS)
    // ═══════════════════════════════════════════════════════════════

    private async Task BuildModuleDiagnosticsSection(string moduleId, string level, string title, int borderBg)
    {
        // Fetch diagnostic entries
        List<DiagnosticEntryDto>? entries = null;
        int count = 0;
        try
        {
            using var resp = await Api.GetAsync($"/modules/{moduleId}/diagnostics?level={level}&take=50");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                var root = doc.RootElement;
                count = root.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                if (root.TryGetProperty("entries", out var arr))
                    entries = JsonSerializer.Deserialize<List<DiagnosticEntryDto>>(arr.GetRawText(), Json);
            }
        }
        catch { /* swallow */ }

        Sub($"── {title} ({count}) ──");

        var border = new Border
        {
            Background = B(borderBg),
            BorderBrush = B(0x333333),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel { Spacing = 6 };

        if (entries is null or { Count: 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"No {level}s recorded this session.",
                FontFamily = Mono, FontSize = 10, Foreground = B(0x555555),
            });
        }
        else
        {
            foreach (var e in entries)
            {
                var entryPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
                var ts = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                var lvlTag = $"[{e.Level[..3].ToUpperInvariant()}]";

                entryPanel.Children.Add(new TextBlock
                {
                    Text = $"{ts} {lvlTag} {e.Message}",
                    FontFamily = Mono, FontSize = 10, Foreground = B(0xCCCCCC),
                    TextWrapping = TextWrapping.Wrap,
                });

                if (e.ExceptionType is not null || e.StackTrace is not null)
                {
                    var detail = e.ExceptionType ?? "";
                    if (e.StackTrace is not null)
                    {
                        // Show first stack frame only
                        var firstLine = e.StackTrace.Split('\n', 2)[0].Trim();
                        if (!string.IsNullOrEmpty(firstLine))
                            detail += $"\n  {firstLine}";
                    }
                    entryPanel.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontFamily = Mono, FontSize = 9, Foreground = B(0x888888),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                panel.Children.Add(entryPanel);
            }
        }

        border.Child = panel;
        ContentPanel.Children.Add(border);

        // Refresh button
        var refreshBtn = new Button
        {
            Content = new TextBlock { Text = "↻ Refresh", FontFamily = Mono, FontSize = 10, Foreground = B(0x808080) },
            Background = B(0x1A1A1A), BorderBrush = B(0x333333), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3), Margin = new Thickness(0, 4, 0, 0),
        };
        refreshBtn.Click += async (_, _) =>
        {
            // Check if module still exists
            var state = _cachedModuleStates?.FirstOrDefault(m => m.ModuleId == moduleId);
            if (state is null)
            {
                SelectTab("Manage Modules");
                return;
            }
            await LoadModuleDetailAsync(moduleId);
        };
        ContentPanel.Children.Add(refreshBtn);
    }

    [ImplicitKeys(IsEnabled = false)]
    private sealed record DiagnosticEntryDto(
        DateTimeOffset Timestamp,
        string Level,
        string Message,
        string? ExceptionType,
        string? StackTrace);
}
