using SharpClaw.Services;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Presentation;

public sealed partial class SettingsPage
{
    private CancellationTokenSource? _settingsLifetime;
    private CancellationTokenSource? _tabLifetime;
    private readonly Dictionary<string, SharpClawModuleSettingsPage> _modulePages = new(StringComparer.Ordinal);
    private CancellationToken TabToken => _tabLifetime?.Token ?? CancellationToken.None;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private async Task LoadModuleTabsAsync(CancellationToken token)
    {
        _modulePages.Clear();
        try
        {
            var pages = await ModuleSettingsClient.ReadPagesAsync(Api, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            foreach (var group in pages.GroupBy(page => page.SourceId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                AddTabSection(group.First().ModuleName);
                foreach (var page in group)
                {
                    var key = $"module/{page.SourceId}/{page.Id}";
                    _modulePages.Add(key, page);
                    AddTabButton(page.Title, $"sharpclaw settings {page.SourceId} {page.Id}", key);
                }
            }
            HighlightTabs();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            // Offline boot/settings must not depend on successful Runtime or module discovery.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private async Task LoadTabAsync(string tab, CancellationToken token)
    {
        try
        {
            if (_modulePages.TryGetValue(tab, out var page)) await LoadModuleSettingsAsync(page, token).ConfigureAwait(true);
            else if (string.Equals(tab, "Runtime", StringComparison.Ordinal)) await LoadRuntimeAsync(token).ConfigureAwait(true);
            else if (string.Equals(tab, "Modules", StringComparison.Ordinal)) LoadInstalledModules(token);
            else if (string.Equals(tab, "About", StringComparison.Ordinal)) await LoadAboutAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            if (!token.IsCancellationRequested) Lbl("Settings unavailable. Return to Boot or retry after checking Runtime.", 0x808080);
        }
    }

    private async Task LoadModuleSettingsAsync(SharpClawModuleSettingsPage page, CancellationToken token)
    {
        H(page.Title);
        Lbl(page.ModuleName, 0x808080);
        var document = await ModuleSettingsClient.ReadDocumentAsync(Api, page, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        var readers = new Dictionary<string, Func<object?>>(StringComparer.Ordinal);
        foreach (var field in document.Fields) AddModuleField(field, document.Values, readers);
        var save = TerminalButton("Save");
        var status = StatusBlock();
        ContentPanel.Children.Add(save);
        ContentPanel.Children.Add(status);
        save.Click += (_, _) => ClientUiEvent.Observe(() => SaveModuleSettingsAsync(page, document, readers, save, status, token));
    }

    private void AddModuleField(SharpClawModuleSettingsField field,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, Dictionary<string, Func<object?>> readers)
    {

        Lbl(field.Label, 0x808080);
        var hasValue = values.TryGetValue(field.Key, out var value);
        switch (field.Kind)
        {
            case "boolean":
                var toggle = new ToggleSwitch
                {
                    FontFamily = Mono,
                    FontSize = 11,
                    IsOn = hasValue && value.ValueKind == System.Text.Json.JsonValueKind.True,
                };
                ContentPanel.Children.Add(toggle);
                readers.Add(field.Key, () => toggle.IsOn);
                break;
            case "choice":
                var choice = new ComboBox
                {
                    ItemsSource = field.Choices,
                    MinWidth = 320,
                    SelectedItem = hasValue && value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? value.GetString() : null,
                };
                ContentPanel.Children.Add(choice);
                readers.Add(field.Key, () => choice.SelectedItem as string);
                break;
            case "secret":
                // Never render returned secret values, or clear a credential merely by leaving the box blank.
                var secret = new PasswordBox { MinWidth = 320, PlaceholderText = "Leave blank to keep unchanged" };
                ContentPanel.Children.Add(secret);
                readers.Add(field.Key, () => string.IsNullOrEmpty(secret.Password) ? null : secret.Password);
                break;
            default:
                var input = MakeInput(field.Label);
                input.MaxLength = 4096;
                input.Text = hasValue && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : string.Empty;
                ContentPanel.Children.Add(input);
                readers.Add(field.Key, () => input.Text);
                break;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private static async Task SaveModuleSettingsAsync(SharpClawModuleSettingsPage page,
        SharpClawModuleSettingsDocument document, IReadOnlyDictionary<string, Func<object?>> readers,
        Button save, TextBlock status, CancellationToken token)
    {

        if (!save.IsEnabled || token.IsCancellationRequested) return;
        var values = readers.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);
        if (document.Fields.Any(field => field.Required && !string.Equals(field.Kind, "secret", StringComparison.Ordinal) &&
            (values[field.Key] is null || values[field.Key] is string text && string.IsNullOrWhiteSpace(text))))
        { status.Text = "Complete the required settings."; return; }
        foreach (var field in document.Fields.Where(field => string.Equals(field.Kind, "secret", StringComparison.Ordinal) && values[field.Key] is null))
            values.Remove(field.Key);
        save.IsEnabled = false;
        try
        {
            await ModuleSettingsClient.SaveAsync(Api, page, values, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested) status.Text = "Saved. The module owns validation and application of its settings.";
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            if (!token.IsCancellationRequested) status.Text = "The module did not accept the settings update; no secret values are shown.";
        }
        finally { save.IsEnabled = !token.IsCancellationRequested; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void LoadInstalledModules(CancellationToken pageToken)
    {
        H("Modules");
        Lbl("Install modules from Boot. Enablement changes take effect in a new Runtime graph, not a second loader.", 0x808080);
        var services = App.Services!;
        var store = services.GetRequiredService<ModulePackageStore>();
        var frontend = services.GetRequiredService<FrontendInstanceService>();
        var backend = services.GetRequiredService<BackendProcessManager>();
        var proxyConfigured = RemoteConnection.IsProxyConfigured();
        if (proxyConfigured)
            Lbl("These are local modules. Disconnect the remote backend before installing or changing local enablement. Remote module settings remain available in their own tabs.", 0x808080);
        var modules = store.ReadInstalled().Concat(ModulePackageStore.ReadIdentities(
            Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"), true)).ToArray();
        if (modules.Length == 0) Lbl("No modules installed.", 0x808080);
        foreach (var module in modules)
        {
            Lbl($"{module.DisplayName} / {module.Version} / {(module.Bundled ? "bundled" : "installed")}", 0xCCCCCC);
            var enabled = BundledModuleSetup.IsEnabled(frontend, module.Id, module.DefaultEnabled);
            var toggle = TerminalButton(enabled ? "Disable" : "Enable");
            toggle.IsEnabled = !proxyConfigured;
            var status = StatusBlock();
            ContentPanel.Children.Add(toggle);
            ContentPanel.Children.Add(status);
            toggle.Click += (_, _) => ClientUiEvent.Observe(async () =>
            {
                if (!toggle.IsEnabled || pageToken.IsCancellationRequested) return;
                toggle.IsEnabled = false;
                try
                {
                    await Actions.RunCommandAsync("client.module.enablement", async token =>
                    {
                        await BundledModuleSetup.StopAsync(backend, Gateway, frontend, token).ConfigureAwait(true);
                        await BundledModuleSetup.ConfigureAsync(frontend, store.ActiveRoot, [module], !enabled, token).ConfigureAwait(true);
                    }, pageToken).ConfigureAwait(true);
                    await services.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Boot", cancellationToken: pageToken).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception); status.Text = "Enablement update failed or the selected Runtime is not owned by this frontend.";
                }
                finally { toggle.IsEnabled = !pageToken.IsCancellationRequested && !proxyConfigured; }
            });
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private async Task LoadAboutAsync(CancellationToken token)
    {
        H("SharpClaw");
        Lbl("Boot, modular Settings, stateless debug chat and remote backend connection. All application features belong to modules.", 0x808080);
        var legal = TerminalButton("Open licences and written source offers");
        ContentPanel.Children.Add(legal);
        legal.Click += (_, _) => ClientUiEvent.Observe(async () =>
        {
            try
            {
                await Actions.RunCommandAsync("client.notices.open", async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = Path.Combine(AppContext.BaseDirectory, "legal");
                    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Packaged legal notices are unavailable.");
                    if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(directory)))
                        throw new InvalidOperationException("The platform did not open the notices directory.");
                }, token).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception); if (!token.IsCancellationRequested) Lbl("Notices are in the packaged legal folder; this platform could not open it.", 0x808080);
            }
        });
        var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/PRIVACY_POLICY.txt"));
        var text = await Windows.Storage.FileIO.ReadTextAsync(file);
        token.ThrowIfCancellationRequested();
        Lbl(text, 0x808080);
    }
}
