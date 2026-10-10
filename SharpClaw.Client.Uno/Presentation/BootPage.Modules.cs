using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class BootPage
{
    private CancellationTokenSource? _moduleInspection;
    private PreparedModulePackage? _moduleCandidate;
    private bool _moduleBusy;
    private bool _moduleInstalling;
    private bool _settingAssets;

    private static ModulePackageStore ModuleStore => App.Services!.GetRequiredService<ModulePackageStore>();
    private static ClientActionDispatcher Actions => App.Services!.GetRequiredService<ClientActionDispatcher>();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        RetireBootWork();
        base.OnNavigatedFrom(e);
    }

    private void RetireBootWork()
    {
        _isActive = false;
        StopDots();
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = null;
        _moduleInspection?.Cancel();
        _moduleInspection?.Dispose();
        _moduleInspection = null;
        _moduleCandidate?.Dispose();
        _moduleCandidate = null;
        ModuleGitHubToken.Password = string.Empty;
    }

    private void OnInstallModulesClick(object sender, RoutedEventArgs e)
    {
        ModuleInstallPanel.Visibility = ModuleInstallPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void OnSettingsClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_moduleInstalling) return;
        if (_retryCts is { } retry) await retry.CancelAsync().ConfigureAwait(true);
        try { await App.Services!.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Settings", cancellationToken: CancellationToken.None).ConfigureAwait(true); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception); ModuleInstallStatus.Text = "Navigation was not accepted. Please retry.";
        }
    });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void OnStatelessChatClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_moduleInstalling) return;
        if (_retryCts is { } retry) await retry.CancelAsync().ConfigureAwait(true);
        try { await App.Services!.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Main", cancellationToken: CancellationToken.None).ConfigureAwait(true); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception); ModuleInstallStatus.Text = "Navigation was not accepted. Please retry.";
        }
    });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI navigation failure is translated into a sanitized status and bounded exception metadata; the action owns navigation and the existing installation guard prevents concurrent activation.")]
    private void OnRemoteConnectionClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_moduleInstalling) return;
        if (_retryCts is { } retry) await retry.CancelAsync().ConfigureAwait(true);
        try { await App.Services!.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "RemoteConnection", cancellationToken: CancellationToken.None).ConfigureAwait(true); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            ModuleInstallStatus.Text = "Navigation was not accepted. Please retry.";
        }
    });

    private void OnModuleSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (_moduleBusy || ModuleConfirmButton is null) return;
        _moduleCandidate?.Dispose();
        _moduleCandidate = null;
        ModuleConfirmButton.Visibility = Visibility.Collapsed;
        _settingAssets = true;
        ModuleAssetPicker.ItemsSource = null;
        ModuleAssetPicker.Visibility = Visibility.Collapsed;
        _settingAssets = false;
    }

    private void SetModuleBusy(bool busy)
    {
        _moduleBusy = busy;
        ModuleSourceInput.IsEnabled = !busy;
        ModuleGitHubToken.IsEnabled = !busy;
        ModuleInspectButton.IsEnabled = !busy;
        ModuleAssetPicker.IsEnabled = !busy;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void OnInspectModuleClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_moduleBusy) return;
        SetModuleBusy(true);
        var previous = _moduleInspection;
        var inspection = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _moduleInspection = inspection;
        var token = inspection.Token;
        if (previous is not null)
        {
            try { await previous.CancelAsync().ConfigureAwait(true); }
            finally { previous.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
        _moduleCandidate?.Dispose();
        _moduleCandidate = null;
        ModuleConfirmButton.Visibility = Visibility.Collapsed;
        var sourceText = ModuleSourceInput.Text;
        var credential = ModuleGitHubToken.Password;
        ModuleInstallStatus.Text = "Resolving module source; no module code is executed.";
        try
        {
            var assets = await Actions.RunCommandAsync("client.module.resolve",
                ct => new ValueTask<IReadOnlyList<ModulePackageSource>>(ModuleStore.ResolveAsync(
                    sourceText, credential, ct)), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _settingAssets = true;
            ModuleAssetPicker.ItemsSource = assets;
            ModuleAssetPicker.SelectedItem = null;
            ModuleAssetPicker.Visibility = assets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingAssets = false;
            if (assets.Count == 1) await PrepareModuleAsync(assets[0], token).ConfigureAwait(true);
            else ModuleInstallStatus.Text = "Select the exact release asset or package version to inspect.";
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            if (!token.IsCancellationRequested)
                ModuleInstallStatus.Text = "Unable to inspect this source. Check the local path/package link and required GitHub read:packages credential.";
        }
        finally { _settingAssets = false; SetModuleBusy(false); }
    });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void OnModuleAssetChanged(object sender, SelectionChangedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_settingAssets || _moduleBusy || ModuleAssetPicker.SelectedItem is not ModulePackageSource source) return;
        SetModuleBusy(true);
        _moduleInspection?.Dispose();
        _moduleInspection = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = _moduleInspection.Token;
        try { await PrepareModuleAsync(source, token).ConfigureAwait(true); }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            if (!token.IsCancellationRequested) ModuleInstallStatus.Text = "The selected asset is not a compatible module payload or could not be read. Nothing was activated.";
        }
        finally { SetModuleBusy(false); }
    });

    private async Task PrepareModuleAsync(ModulePackageSource source, CancellationToken token)
    {
        _moduleCandidate?.Dispose();
        _moduleCandidate = null;
        ModuleConfirmButton.Visibility = Visibility.Collapsed;
        ModuleInstallStatus.Text = "Inspecting bounded payload and module manifests; no code is executed.";
        PreparedModulePackage? prepared = null;
        try
        {
            await Actions.RunCommandAsync("client.module.inspect", async ct =>
            {
                prepared = await ModuleStore.PrepareAsync(source, ModuleGitHubToken.Password, ct).ConfigureAwait(true);
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _moduleCandidate = prepared;
            prepared = null;
            ModuleInstallStatus.Text = string.Join('\n', _moduleCandidate!.Modules.Select(module =>
                $"{module.DisplayName} / {module.Id} / {module.Version}")) +
                "\nInstall only code you trust. Confirmation enables it in a new Runtime graph; this is not a signature or safety certification.";
            var proxyConfigured = App.Services!.GetRequiredService<RemoteBackendConnectionService>().IsProxyConfigured();
            ModuleConfirmButton.IsEnabled = !proxyConfigured;
            if (proxyConfigured)
                ModuleInstallStatus.Text += "\nDisconnect the remote backend before installing and enabling local modules.";
            ModuleConfirmButton.Visibility = Visibility.Visible;
        }
        finally { prepared?.Dispose(); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "This UI operation translates failed actions, payload reads or rendering into the existing sanitized failure status. The exception type is journalled; cancellation and retired-page guards prevent late success publication.")]
    private void OnConfirmModuleClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_moduleBusy || _moduleCandidate is not { } candidate) return;
        using var candidateOwner = candidate;
        _moduleCandidate = null; // Transfer the payload to this operation before cancellation can suspend.
        SetModuleBusy(true);
        _moduleInstalling = true;
        ModuleConfirmButton.IsEnabled = false;
        if (_retryCts is { } retry) await retry.CancelAsync().ConfigureAwait(true);
        var services = App.Services!;
        var backend = services.GetRequiredService<BackendProcessManager>();
        var frontend = services.GetRequiredService<FrontendInstanceService>();
        var committed = false;
        var connectionClaimed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            // Drain the canceled boot probe before stopping/committing; it cannot restart behind this operation.
            await _connectionGate.WaitAsync(deadline.Token).ConfigureAwait(true);
            connectionClaimed = true;
            BundledModuleSetup.RequireLocalMode(frontend);
            BundledModuleSetup.RequireOwnedTarget(backend);
            await ExecuteConfirmedInstallAsync(candidate, backend, frontend, services,
                () => committed = true, deadline.Token).ConfigureAwait(true);
            if (!committed) throw new InvalidOperationException("Installation was suppressed.");
            _connectionGate.Release();
            connectionClaimed = false;
            await RestartAfterInstallationAsync(candidate).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            candidate.Dispose();
            _moduleCandidate = null;
            ModuleConfirmButton.Visibility = Visibility.Collapsed;
            ModuleInstallStatus.Text = committed
                ? "The module was installed and enabled, but the new Runtime did not become ready. Open Settings to disable it or inspect Boot diagnostics."
                : "Installation was not completed. Inspect again after checking compatibility, duplicate identities and bundled Runtime ownership. Existing installations were not overwritten.";
        }
        finally
        {
            if (connectionClaimed) _connectionGate.Release();
            _moduleInstalling = false;
            SetModuleBusy(false);
            ModuleConfirmButton.IsEnabled = true;
        }
    });
    private static async Task ExecuteConfirmedInstallAsync(
        PreparedModulePackage candidate, BackendProcessManager backend,
        FrontendInstanceService frontend, IServiceProvider services,
        Action onCommitted, CancellationToken cancellationToken)
    {
        var invoked = 0;
        await Actions.RunCommandAsync("client.module.install", async token =>
        {
            if (Interlocked.Exchange(ref invoked, 1) != 0)
                throw new InvalidOperationException("The confirmed installation may run only once.");
            await ModuleStore.CommitAsync(candidate,
                Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"),
                ct => BundledModuleSetup.StopAsync(backend, services.GetService<GatewayProcessManager>(), frontend, ct),
                (root, modules, ct) => BundledModuleSetup.ConfigureAsync(frontend, root, modules, true, ct), token).ConfigureAwait(true);
            onCommitted();
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task RestartAfterInstallationAsync(PreparedModulePackage candidate)
    {
        candidate.Dispose();
        if (!_isActive) return;
        _moduleCandidate = null;
        ModuleConfirmButton.Visibility = Visibility.Collapsed;
        ModuleGitHubToken.Password = string.Empty;
        ModuleInstallStatus.Text = "Installed. Starting a new Runtime graph through the normal module loader.";
        _retryCts?.Dispose();
        _retryCts = new CancellationTokenSource();
        await RunConnectionFlowAsync(null, _retryCts.Token).ConfigureAwait(true);
    }

}
