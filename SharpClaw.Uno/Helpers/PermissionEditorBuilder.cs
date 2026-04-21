using System.Text.Json;
using SharpClaw.Services;

namespace SharpClaw.Helpers;

/// <summary>
/// Shared builder that constructs a permission editor UI (global flags + per-resource grants)
/// into any <see cref="StackPanel"/>.  Used by SettingsPage (role editor), FirstSetupPage
/// (initial role grants), and MainPage channel-settings (pre-authorization overrides).
///
/// Configuration via fluent API:
/// <list type="bullet">
///   <item><see cref="WithCallerFilter"/> – restrict visible flags/resources to what the caller holds</item>
///   <item><see cref="WithExisting"/>    – pre-populate from an existing permissions JSON payload</item>
///   <item><see cref="WithFlagClearance"/>  – show per-flag clearance combo (default: true)</item>
///   <item><see cref="WithGrantClearance"/> – show per-grant clearance combo (default: true)</item>
///   <item><see cref="WithAutoSuggestBox"/> – use AutoSuggestBox instead of ComboBox for resource selectors</item>
/// </list>
/// </summary>
internal sealed class PermissionEditorBuilder
{
    private readonly SharpClawApiClient _api;

    // ── Configuration ────────────────────────────────────────────
    private JsonElement? _callerFilter;
    private JsonElement? _existing;
    private bool _flagClearance = true;
    private bool _grantClearance = true;
    private bool _useAutoSuggest;

    /// <summary>
    /// Optional callback invoked whenever the user manually edits a permission
    /// (unchecks a flag, removes a grant row, etc.). Used by callers to reset
    /// "grant all" state.
    /// </summary>
    private Action? _onManualEdit;

    // ── State (populated during Build*, read during Collect*) ────
    private readonly Dictionary<string, (CheckBox Check, ComboBox? Clearance)> _flagEditors = new(7);
    private readonly Dictionary<string, StackPanel> _grantPanels = new(13);
    private readonly Dictionary<Guid, string> _nameCache = [];
    private readonly Dictionary<string, List<(Guid Id, string Name)>> _resourcesByType = [];

    public PermissionEditorBuilder(SharpClawApiClient api) => _api = api;

    // ── Fluent config ────────────────────────────────────────────

    /// <summary>Restrict visible flags and resource types to those the caller holds.</summary>
    public PermissionEditorBuilder WithCallerFilter(JsonElement? callerPermissions)
    { _callerFilter = callerPermissions; return this; }

    /// <summary>Pre-populate checkboxes and grant rows from an existing permissions payload.</summary>
    public PermissionEditorBuilder WithExisting(JsonElement? existing)
    { _existing = existing; return this; }

    /// <summary>Show a clearance combo under each global flag (default: true).</summary>
    public PermissionEditorBuilder WithFlagClearance(bool enabled)
    { _flagClearance = enabled; return this; }

    /// <summary>Show a clearance combo on each resource grant row (default: true).</summary>
    public PermissionEditorBuilder WithGrantClearance(bool enabled)
    { _grantClearance = enabled; return this; }

    /// <summary>Use <see cref="AutoSuggestBox"/> instead of <see cref="ComboBox"/> for adding resources.</summary>
    public PermissionEditorBuilder WithAutoSuggestBox(bool enabled)
    { _useAutoSuggest = enabled; return this; }

    /// <summary>Register a callback invoked when the user manually edits any permission.</summary>
    public PermissionEditorBuilder WithManualEditCallback(Action? callback)
    { _onManualEdit = callback; return this; }

    // ── Build methods ────────────────────────────────────────────

    /// <summary>
    /// Builds the global flags section into <paramref name="container"/>.
    /// </summary>
    /// <returns><c>true</c> if at least one flag was added.</returns>
    public bool BuildGlobalFlags(StackPanel container)
    {
        _flagEditors.Clear();
        var panel = new StackPanel { Spacing = _flagClearance ? 10 : 4 };

        // Resolve the globalFlags sub-objects from existing/caller JSON.
        JsonElement? existingFlags = _existing is { } ex
            && ex.TryGetProperty("globalFlags", out var ef) ? ef : null;
        JsonElement? callerFlags = _callerFilter is { } cf
            && cf.TryGetProperty("globalFlags", out var cff) ? cff : null;

        for (var i = 0; i < TerminalUI.GlobalFlagNames.Length; i++)
        {
            var flag = TerminalUI.GlobalFlagNames[i];

            // Caller filtering — skip flags the caller doesn't hold
            if (_callerFilter is not null)
            {
                if (callerFlags is null || !callerFlags.Value.TryGetProperty(flag, out _))
                    continue;
            }

            // A flag is "on" if the key exists in the globalFlags dict.
            var on = existingFlags is { } ef2 && ef2.TryGetProperty(flag, out _);
            var cb = new CheckBox
            {
                IsChecked = on, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(4, 0, 0, 0),
                Content = new TextBlock
                {
                    Text = TerminalUI.FormatFlagName(flag),
                    FontFamily = TerminalUI.Mono, FontSize = 11,
                    Foreground = TerminalUI.Brush(0xE0E0E0),
                },
            };
            cb.Unchecked += (_, _) => _onManualEdit?.Invoke();
            if (TerminalUI.GlobalFlagTooltips.TryGetValue(flag, out var tip))
                ToolTipService.SetToolTip(cb, tip);

            ComboBox? clrBox = null;
            if (_flagClearance)
            {
                // Clearance is now the dictionary value.
                var cl = existingFlags is { } ef3
                    && ef3.TryGetProperty(flag, out var clrProp)
                        ? clrProp.GetString() ?? "Unset" : "Unset";

                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(cb);
                row.Children.Add(new TextBlock
                {
                    Text = "Clearance:", FontFamily = TerminalUI.Mono, FontSize = 9,
                    Foreground = TerminalUI.Brush(0x808080), Margin = new Thickness(24, 2, 0, 0),
                });
                clrBox = TerminalUI.MakeClearanceCombo(cl, includeUnset: true);
                clrBox.Margin = new Thickness(24, 0, 0, 0);
                row.Children.Add(clrBox);
                panel.Children.Add(row);
            }
            else
            {
                panel.Children.Add(cb);
            }

            _flagEditors[flag] = (cb, clrBox);
        }

        container.Children.Add(panel);
        return _flagEditors.Count > 0;
    }

    /// <summary>
    /// Builds the resource grants section into <paramref name="container"/>.
    /// Performs a parallel fetch of all resource lookups.
    /// </summary>
    public async Task BuildResourceGrantsAsync(StackPanel container)
    {
        _grantPanels.Clear();
        _nameCache.Clear();
        _resourcesByType.Clear();
        await PreloadResourceNamesAsync();

        // Resolve the resourceGrants sub-objects from existing/caller JSON.
        JsonElement? existingGrants = _existing is { } ex
            && ex.TryGetProperty("resourceGrants", out var eg) ? eg : null;
        JsonElement? callerGrants = _callerFilter is { } cf
            && cf.TryGetProperty("resourceGrants", out var cg) ? cg : null;

        var resCont = new StackPanel { Spacing = _grantClearance ? 16 : 10 };

        foreach (var (apiName, displayName) in TerminalUI.ResourceAccessTypes)
        {
            // Caller filtering — skip types the caller has no grants for
            var callerIds = GetCallerResourceIds(callerGrants, apiName);
            if (_callerFilter is not null && callerIds is null)
                continue;

            var section = new StackPanel { Spacing = _grantClearance ? 4 : 2 };
            var header = new TextBlock
            {
                Text = displayName, FontFamily = TerminalUI.Mono, FontSize = 12,
                Foreground = TerminalUI.Brush(0x00CCFF),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            if (TerminalUI.ResourceAccessTooltips.TryGetValue(apiName, out var resTip))
                ToolTipService.SetToolTip(header, resTip);
            section.Children.Add(header);

            var gp = new StackPanel { Spacing = _grantClearance ? 6 : 2, Margin = new Thickness(12, 0, 0, 0) };
            _grantPanels[apiName] = gp;

            // Populate existing grants from the resourceGrants dict
            if (existingGrants is { } eg2
                && eg2.TryGetProperty(apiName, out var ap) && ap.ValueKind == JsonValueKind.Array)
                foreach (var g in ap.EnumerateArray())
                    if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                    {
                        var cl = g.TryGetProperty("clearance", out var clp) ? clp.GetString() ?? "Independent" : "Independent";
                        AddGrantRow(gp, rid.GetGuid(), cl);
                    }

            section.Children.Add(gp);

            // Add-resource controls
            var capturedApi = apiName;
            var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (_useAutoSuggest)
                BuildAutoSuggestSelector(actionsRow, capturedApi, apiName);
            else
                BuildComboSelector(actionsRow, capturedApi, callerIds);

            section.Children.Add(actionsRow);
            resCont.Children.Add(section);
        }

        container.Children.Add(resCont);
    }

    // ── Collection methods ───────────────────────────────────────

    /// <summary>Reads the current state of global flag checkboxes (+ optional clearance).</summary>
    public Dictionary<string, object?> CollectFlags()
    {
        var flags = new Dictionary<string, object?>();
        foreach (var (flagKey, ed) in _flagEditors)
        {
            if (ed.Check.IsChecked != true) continue;

            var clearance = _flagClearance && ed.Clearance is { } clr
                && clr.SelectedItem is ComboBoxItem { Tag: string cl }
                    ? cl : "Independent";
            flags[flagKey] = clearance;
        }
        return flags;
    }

    /// <summary>Reads the current state of per-resource grant rows.</summary>
    public Dictionary<string, object?> CollectGrants()
    {
        var grants = new Dictionary<string, object?>();
        foreach (var (apiName, panel) in _grantPanels)
        {
            var list = new List<object>();
            foreach (var child in panel.Children)
            {
                if (child is not StackPanel row) continue;
                var idBlock = row.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Tag is Guid);
                if (idBlock?.Tag is not Guid resId) continue;

                string clearance;
                if (_grantClearance)
                {
                    var clrBox = row.Children.OfType<ComboBox>().FirstOrDefault();
                    clearance = clrBox?.SelectedItem is ComboBoxItem { Tag: string cl } ? cl : "Unset";
                }
                else
                {
                    clearance = "Independent";
                }

                list.Add(new { resourceId = resId, clearance });
            }
            if (list.Count > 0)
                grants[apiName] = list;
        }
        return grants;
    }

    /// <summary>
    /// Merges <see cref="CollectFlags"/> and <see cref="CollectGrants"/> into the
    /// <c>SetRolePermissionsRequest</c> JSON shape.
    /// </summary>
    public Dictionary<string, object?> CollectAll()
    {
        var result = new Dictionary<string, object?>
        {
            ["globalFlags"] = CollectFlags(),
            ["resourceGrants"] = CollectGrants(),
        };
        return result;
    }

    // ── Private helpers ─────────────────────────────────────────

    private void AddGrantRow(StackPanel panel, Guid resId, string clearance)
    {
        var isWildcard = resId == TerminalUI.AllResourcesId;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = _grantClearance ? 10 : 8 };

        string idText;
        if (isWildcard)
            idText = "* (all)";
        else if (_nameCache.TryGetValue(resId, out var name))
            idText = _useAutoSuggest ? $"{name}  ({resId.ToString()[..8]}\u2026)" : name;
        else
            idText = resId.ToString()[..8] + "\u2026";

        var idBlock = new TextBlock
        {
            Text = idText, FontFamily = TerminalUI.Mono, FontSize = 11,
            // Amber foreground signals that wildcard rows are locked/immutable.
            Foreground = isWildcard ? TerminalUI.Brush(0xFFAA00) : TerminalUI.Brush(0xE0E0E0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = _grantClearance ? 140 : 80, Tag = resId,
        };
        if (!isWildcard) ToolTipService.SetToolTip(idBlock, resId.ToString());
        row.Children.Add(idBlock);

        if (_grantClearance)
        {
            row.Children.Add(new TextBlock
            {
                Text = "Clearance:", FontFamily = TerminalUI.Mono, FontSize = 9,
                Foreground = TerminalUI.Brush(0x808080), VerticalAlignment = VerticalAlignment.Center,
            });
            var clrCombo = TerminalUI.MakeClearanceCombo(clearance);
            // Wildcard clearance is immutable — lock the combo.
            if (isWildcard) clrCombo.IsEnabled = false;
            row.Children.Add(clrCombo);
        }

        // Wildcard rows cannot be removed — omit the remove button.
        if (!isWildcard)
        {
            var rm = TerminalUI.RemoveButton(() =>
            {
                panel.Children.Remove(row);
                _onManualEdit?.Invoke();
            });
            rm.Padding = new Thickness(2);
            if (rm.Content is TextBlock rmTb) rmTb.FontSize = 9;
            row.Children.Add(rm);
        }

        panel.Children.Add(row);
    }

    private void BuildAutoSuggestSelector(StackPanel actionsRow, string capturedApi, string apiName)
    {
        // Wildcard button — hidden if a wildcard row already exists in the panel.
        var addWc = new Button
        {
            Content = new TextBlock { Text = "+ wildcard", FontFamily = TerminalUI.Mono, FontSize = 10, Foreground = TerminalUI.Brush(0x00FF00) },
            Background = TerminalUI.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 2, 4, 2), MinWidth = 0, MinHeight = 0,
        };
        addWc.Click += (_, _) =>
        {
            if (_grantPanels.TryGetValue(capturedApi, out var panel) && !PanelHasWildcard(panel))
                AddGrantRow(panel, TerminalUI.AllResourcesId, "Independent");
        };
        actionsRow.Children.Add(addWc);

        // AutoSuggestBox
        if (_resourcesByType.TryGetValue(apiName, out var lookupItems) && lookupItems.Count > 0)
        {
            var resDisplayMap = new Dictionary<string, Guid>(lookupItems.Count);
            foreach (var (id, name) in lookupItems)
                resDisplayMap.TryAdd($"{name}  ({id.ToString()[..8]}\u2026)", id);
            var resSearch = new AutoSuggestBox
            {
                PlaceholderText = "+ add resource\u2026", FontFamily = TerminalUI.Mono,
                FontSize = 10, MinWidth = 200,
            };
            ToolTipService.SetToolTip(resSearch, "Search for a specific resource to grant access to");
            resSearch.TextChanged += (sender, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var q = sender.Text.Trim();
                sender.ItemsSource = string.IsNullOrEmpty(q)
                    ? resDisplayMap.Keys.ToList()
                    : resDisplayMap.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            };
            resSearch.QuerySubmitted += (sender, args) =>
            {
                var chosen = args.ChosenSuggestion?.ToString();
                if (chosen is null || !resDisplayMap.TryGetValue(chosen, out var resId)) return;
                sender.Text = string.Empty;
                if (_grantPanels.TryGetValue(capturedApi, out var panel))
                    AddGrantRow(panel, resId, "Independent");
            };
            actionsRow.Children.Add(resSearch);
        }
    }

    private void BuildComboSelector(StackPanel actionsRow, string capturedApi, HashSet<Guid>? callerIds)
    {
        actionsRow.Children.Add(new TextBlock
        {
            Text = "Resource:", FontFamily = TerminalUI.Mono, FontSize = 10,
            Foreground = TerminalUI.Brush(0x00CCFF), VerticalAlignment = VerticalAlignment.Center,
        });
        var resSelector = new ComboBox
        {
            FontFamily = TerminalUI.Mono, FontSize = 10,
            Background = TerminalUI.Brush(0x0A1A2A), Foreground = TerminalUI.Brush(0x00CCFF),
            BorderBrush = TerminalUI.Brush(0x00CCFF), BorderThickness = new Thickness(1),
            MinWidth = 220,
        };
        PopulateResourceSelector(resSelector, capturedApi, callerIds);
        var addBtn = new Button
        {
            Content = new TextBlock { Text = "+ add", FontFamily = TerminalUI.Mono, FontSize = 10, Foreground = TerminalUI.Brush(0x00FF00) },
            Background = TerminalUI.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2), MinWidth = 0, MinHeight = 0,
        };
        addBtn.Click += (_, _) =>
        {
            if (resSelector.SelectedItem is not ComboBoxItem { Tag: Guid resId } || resId == Guid.Empty) return;
            if (!_grantPanels.TryGetValue(capturedApi, out var panel)) return;
            foreach (var child in panel.Children)
                if (child is StackPanel r && r.Children.Count > 0
                    && r.Children[0] is TextBlock { Tag: Guid existing } && existing == resId)
                    return;
            AddGrantRow(panel, resId, "Independent");
        };
        actionsRow.Children.Add(resSelector);
        actionsRow.Children.Add(addBtn);
    }

    private async Task PreloadResourceNamesAsync()
    {
        var tasks = TerminalUI.ResourceAccessTypes.Select(async r =>
        {
            try
            {
                using var resp = await _api.GetAsync($"/resources/lookup/{r.ApiName}");
                if (!resp.IsSuccessStatusCode) return;
                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                var items = new List<(Guid Id, string Name)>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetGuid();
                    var name = item.GetProperty("name").GetString() ?? id.ToString()[..8];
                    _nameCache[id] = name;
                    items.Add((id, name));
                }
                _resourcesByType[r.ApiName] = items;
            }
            catch { /* swallow */ }
        });
        await Task.WhenAll(tasks);
    }

    private static HashSet<Guid>? GetCallerResourceIds(JsonElement? callerGrants, string accessType)
    {
        if (callerGrants is not { } perms) return null;
        if (!perms.TryGetProperty(accessType, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var ids = new HashSet<Guid>();
        foreach (var g in arr.EnumerateArray())
            if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                ids.Add(rid.GetGuid());
        return ids.Count > 0 ? ids : null;
    }

    private void PopulateResourceSelector(ComboBox selector, string accessType, HashSet<Guid>? callerIds)
    {
        // If caller has no grants for this type, show disabled placeholder
        if (_callerFilter is not null && callerIds is null)
        {
            selector.Items.Add(new ComboBoxItem { Content = "(no access)", Tag = Guid.Empty, IsEnabled = false });
            selector.SelectedIndex = 0;
            return;
        }

        var hasWildcard = callerIds is not null && callerIds.Contains(TerminalUI.AllResourcesId);

        // Only offer the wildcard option if the panel does not already have one —
        // adding a second wildcard would be rejected by the server and is meaningless.
        var panelHasWildcard = _grantPanels.TryGetValue(accessType, out var gp) && PanelHasWildcard(gp);
        if (!panelHasWildcard && (callerIds is null || hasWildcard))
            selector.Items.Add(new ComboBoxItem { Content = "* (all resources)", Tag = TerminalUI.AllResourcesId });

        if (_resourcesByType.TryGetValue(accessType, out var items))
            foreach (var (id, name) in items)
                if (callerIds is null || hasWildcard || callerIds.Contains(id))
                    selector.Items.Add(new ComboBoxItem { Content = name, Tag = id });

        if (selector.Items.Count > 0)
            selector.SelectedIndex = 0;
    }

    /// <summary>
    /// Returns <c>true</c> if the grant panel already contains a wildcard
    /// (<see cref="TerminalUI.AllResourcesId"/>) row.
    /// </summary>
    private static bool PanelHasWildcard(StackPanel panel)
    {
        foreach (var child in panel.Children)
            if (child is StackPanel row
                && row.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Tag is Guid) is { Tag: Guid id }
                && id == TerminalUI.AllResourcesId)
                return true;
        return false;
    }

    // ── Module-grouped build ─────────────────────────────────────

    /// <summary>
    /// Builds the entire permission editor (flags + grants) grouped by owning module.
    /// Each enabled module gets a collapsible section header, its flags, and its resource grants.
    /// </summary>
    public async Task BuildGroupedByModuleAsync(
        StackPanel container,
        List<ModulePermissionMetadata> metadata)
    {
        _flagEditors.Clear();
        _grantPanels.Clear();
        _nameCache.Clear();
        _resourcesByType.Clear();
        await PreloadResourceNamesAsync();

        var sorted = TerminalUI.TopologicalSort(metadata);

        foreach (var module in sorted)
        {
            if (!module.Enabled) continue;
            if (module.GlobalFlags.Count == 0 && module.ResourceTypes.Count == 0) continue;

            // ── Module section header ──
            var header = new TextBlock
            {
                Text = $"── {module.DisplayName} ──",
                FontFamily = TerminalUI.Mono, FontSize = 12,
                Foreground = TerminalUI.Brush(0x00CCFF),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4),
            };
            container.Children.Add(header);

            BuildSingleModule(container, module);
        }
    }

    // ── Single-module build ──────────────────────────────────────

    /// <summary>
    /// Ensures resource name caches are populated.
    /// Call once before using <see cref="BuildSingleModule"/>.
    /// </summary>
    public async Task EnsureResourcesLoadedAsync()
    {
        if (_resourcesByType.Count == 0)
            await PreloadResourceNamesAsync();
    }

    /// <summary>
    /// Builds a single module's global flags and resource grants into
    /// <paramref name="container"/>.
    /// </summary>
    public void BuildSingleModule(StackPanel container, ModulePermissionMetadata module)
    {
        JsonElement? existingFlags = _existing is { } ex
            && ex.TryGetProperty("globalFlags", out var ef) ? ef : null;
        JsonElement? callerFlags = _callerFilter is { } cf
            && cf.TryGetProperty("globalFlags", out var cff) ? cff : null;
        JsonElement? existingGrants = _existing is { } exg
            && exg.TryGetProperty("resourceGrants", out var eg) ? eg : null;
        JsonElement? callerGrants = _callerFilter is { } cfg
            && cfg.TryGetProperty("resourceGrants", out var cg) ? cg : null;

        // ── Global flags for this module ──
        if (module.GlobalFlags.Count > 0)
        {
            var flagPanel = new StackPanel { Spacing = _flagClearance ? 10 : 4, Margin = new Thickness(8, 0, 0, 0) };

            foreach (var flag in module.GlobalFlags)
            {
                if (_callerFilter is not null)
                {
                    if (callerFlags is null || !callerFlags.Value.TryGetProperty(flag.FlagKey, out _))
                        continue;
                }

                var on = existingFlags is { } ef2 && ef2.TryGetProperty(flag.FlagKey, out _);
                var cb = new CheckBox
                {
                    IsChecked = on, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(4, 0, 0, 0),
                    Content = new TextBlock
                    {
                        Text = flag.DisplayName,
                        FontFamily = TerminalUI.Mono, FontSize = 11,
                        Foreground = TerminalUI.Brush(0xE0E0E0),
                    },
                };
                cb.Unchecked += (_, _) => _onManualEdit?.Invoke();
                if (!string.IsNullOrEmpty(flag.Description))
                    ToolTipService.SetToolTip(cb, flag.Description);

                ComboBox? clrBox = null;
                if (_flagClearance)
                {
                    var cl = existingFlags is { } ef3
                        && ef3.TryGetProperty(flag.FlagKey, out var clrProp)
                            ? clrProp.GetString() ?? "Unset" : "Unset";

                    var row = new StackPanel { Spacing = 4 };
                    row.Children.Add(cb);
                    row.Children.Add(new TextBlock
                    {
                        Text = "Clearance:", FontFamily = TerminalUI.Mono, FontSize = 9,
                        Foreground = TerminalUI.Brush(0x808080), Margin = new Thickness(24, 2, 0, 0),
                    });
                    clrBox = TerminalUI.MakeClearanceCombo(cl, includeUnset: true);
                    clrBox.Margin = new Thickness(24, 0, 0, 0);
                    row.Children.Add(clrBox);
                    flagPanel.Children.Add(row);
                }
                else
                {
                    flagPanel.Children.Add(cb);
                }

                _flagEditors[flag.FlagKey] = (cb, clrBox);
            }

            if (flagPanel.Children.Count > 0)
                container.Children.Add(flagPanel);
        }

        // ── Resource types for this module ──
        if (module.ResourceTypes.Count > 0)
        {
            var resCont = new StackPanel { Spacing = _grantClearance ? 10 : 6, Margin = new Thickness(8, 4, 0, 0) };

            foreach (var resType in module.ResourceTypes)
            {
                var callerIds = GetCallerResourceIds(callerGrants, resType.ResourceType);
                if (_callerFilter is not null && callerIds is null)
                    continue;

                var section = new StackPanel { Spacing = _grantClearance ? 4 : 2 };
                var resHeader = new TextBlock
                {
                    Text = resType.DisplayName, FontFamily = TerminalUI.Mono, FontSize = 11,
                    Foreground = TerminalUI.Brush(0x00CCFF),
                };
                if (TerminalUI.ResourceAccessTooltips.TryGetValue(resType.ResourceType, out var resTip))
                    ToolTipService.SetToolTip(resHeader, resTip);
                section.Children.Add(resHeader);

                var gp = new StackPanel { Spacing = _grantClearance ? 6 : 2, Margin = new Thickness(12, 0, 0, 0) };
                _grantPanels[resType.ResourceType] = gp;

                if (existingGrants is { } eg2
                    && eg2.TryGetProperty(resType.ResourceType, out var ap) && ap.ValueKind == JsonValueKind.Array)
                    foreach (var g in ap.EnumerateArray())
                        if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                        {
                            var cl = g.TryGetProperty("clearance", out var clp) ? clp.GetString() ?? "Independent" : "Independent";
                            AddGrantRow(gp, rid.GetGuid(), cl);
                        }

                section.Children.Add(gp);

                var capturedApi = resType.ResourceType;
                var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                if (_useAutoSuggest)
                    BuildAutoSuggestSelector(actionsRow, capturedApi, resType.ResourceType);
                else
                    BuildComboSelector(actionsRow, capturedApi, callerIds);

                section.Children.Add(actionsRow);
                resCont.Children.Add(section);
            }

            if (resCont.Children.Count > 0)
                container.Children.Add(resCont);
        }
    }

    /// <summary>
    /// Removes tracked editors for the given module's flags and resource types.
    /// Call when the user disables a module so <see cref="CollectAll"/> excludes it.
    /// </summary>
    public void ClearModuleEntries(ModulePermissionMetadata module)
    {
        foreach (var flag in module.GlobalFlags)
            _flagEditors.Remove(flag.FlagKey);
        foreach (var res in module.ResourceTypes)
            _grantPanels.Remove(res.ResourceType);
    }

    /// <summary>
    /// Checks all global-flag checkboxes for the given module and sets their
    /// clearance to "Independent".
    /// </summary>
    public void CheckAllFlags(ModulePermissionMetadata module)
    {
        foreach (var flag in module.GlobalFlags)
        {
            if (!_flagEditors.TryGetValue(flag.FlagKey, out var entry)) continue;
            entry.Check.IsChecked = true;
            if (entry.Clearance is { } clr)
            {
                for (var i = 0; i < clr.Items.Count; i++)
                    if (clr.Items[i] is ComboBoxItem { Tag: "Independent" })
                    { clr.SelectedIndex = i; break; }
            }
        }
    }

    /// <summary>
    /// Ensures every resource type in the given module has at least a wildcard
    /// (<c>AllResources</c>) grant row. Existing rows are not duplicated.
    /// </summary>
    public void EnsureWildcardGrants(ModulePermissionMetadata module)
    {
        foreach (var resType in module.ResourceTypes)
        {
            if (!_grantPanels.TryGetValue(resType.ResourceType, out var panel)) continue;

            // Check if wildcard already present
            var hasWildcard = false;
            foreach (var child in panel.Children)
                if (child is StackPanel row && row.Children.Count > 0
                    && row.Children[0] is TextBlock { Tag: Guid id } && id == TerminalUI.AllResourcesId)
                { hasWildcard = true; break; }

            if (!hasWildcard)
                AddGrantRow(panel, TerminalUI.AllResourcesId, "Independent");
        }
    }
}
