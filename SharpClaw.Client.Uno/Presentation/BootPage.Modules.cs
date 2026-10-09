using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class BootPage
{
    private CancellationTokenSource? _moduleInspection;
    private PreparedModulePackage? _moduleCandidate;
    private bool _moduleBusy;
    private bool _moduleInstalling;
    private bool _settingAssets;

    private ModulePackageStore ModuleStore => App.Services!.GetRequiredService<ModulePackageStore>();
    private ClientActionDispatcher Actions => App.Services!.GetRequiredService<ClientActionDispatcher>();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        RetireBootWork();
        base.OnNavigatedFrom(e);
    }

    private void RetireBootWork()
    {
        _isActive = false;
        _retryCts?.Cancel();
        _moduleInspection?.Cancel();
        _moduleCandidate?.Dispose();
        _moduleCandidate = null;
        ModuleGitHubToken.Password = string.Empty;
    }

    private void OnInstallModulesClick(object sender, RoutedEventArgs e)
    {
        ModuleInstallPanel.Visibility = ModuleInstallPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_moduleInstalling) return;
        _retryCts?.Cancel();
        try { await App.Services!.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Settings"); }
        catch (Exception) { ModuleInstallStatus.Text = "Navigation was not accepted. Please retry."; }
    }

    private async void OnStatelessChatClick(object sender, RoutedEventArgs e)
    {
        if (_moduleInstalling) return;
        _retryCts?.Cancel();
        try { await App.Services!.GetRequiredService<ClientNavigationService>().NavigateRouteAsync(this, "Main"); }
        catch (Exception) { ModuleInstallStatus.Text = "Navigation was not accepted. Please retry."; }
    }

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

    private async void OnInspectModuleClick(object sender, RoutedEventArgs e)
    {
        if (_moduleBusy) return;
        SetModuleBusy(true);
        _moduleInspection?.Cancel();
        _moduleInspection?.Dispose();
        _moduleInspection = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = _moduleInspection.Token;
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
                    sourceText, credential, ct)), token);
            token.ThrowIfCancellationRequested();
            _settingAssets = true;
            ModuleAssetPicker.ItemsSource = assets;
            ModuleAssetPicker.SelectedItem = null;
            ModuleAssetPicker.Visibility = assets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _settingAssets = false;
            if (assets.Count == 1) await PrepareModuleAsync(assets[0], token);
            else ModuleInstallStatus.Text = "Select the exact release asset or package version to inspect.";
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                ModuleInstallStatus.Text = "Unable to inspect this source. Check the local path/package link and required GitHub read:packages credential.";
        }
        finally { _settingAssets = false; SetModuleBusy(false); }
    }

    private async void OnModuleAssetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingAssets || _moduleBusy || ModuleAssetPicker.SelectedItem is not ModulePackageSource source) return;
        SetModuleBusy(true);
        _moduleInspection?.Dispose();
        _moduleInspection = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = _moduleInspection.Token;
        try { await PrepareModuleAsync(source, token); }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) ModuleInstallStatus.Text = "The selected asset is not a compatible module payload or could not be read. Nothing was activated.";
        }
        finally { SetModuleBusy(false); }
    }

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
                prepared = await ModuleStore.PrepareAsync(source, ModuleGitHubToken.Password, ct);
            }, token);
            token.ThrowIfCancellationRequested();
            _moduleCandidate = prepared;
            prepared = null;
            ModuleInstallStatus.Text = string.Join("\n", _moduleCandidate!.Modules.Select(module =>
                $"{module.DisplayName} / {module.Id} / {module.Version}")) +
                "\nInstall only code you trust. Confirmation enables it in a new Runtime graph; this is not a signature or safety certification.";
            ModuleConfirmButton.Visibility = Visibility.Visible;
        }
        finally { prepared?.Dispose(); }
    }

    private async void OnConfirmModuleClick(object sender, RoutedEventArgs e)
    {
        if (_moduleBusy || _moduleCandidate is not { } candidate) return;
        SetModuleBusy(true);
        _moduleInstalling = true;
        ModuleConfirmButton.IsEnabled = false;
        _retryCts?.Cancel();
        var services = App.Services!;
        var backend = services.GetRequiredService<BackendProcessManager>();
        var frontend = services.GetRequiredService<FrontendInstanceService>();
        var committed = false;
        var connectionClaimed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            // Drain the canceled boot probe before stopping/committing; it cannot restart behind this operation.
            await _connectionGate.WaitAsync(deadline.Token);
            connectionClaimed = true;
            BundledModuleSetup.RequireOwnedTarget(backend);
            var invoked = 0;
            await Actions.RunCommandAsync("client.module.install", async token =>
            {
                if (Interlocked.Exchange(ref invoked, 1) != 0)
                    throw new InvalidOperationException("The confirmed installation may run only once.");
                await ModuleStore.CommitAsync(candidate,
                    Path.Combine(Path.GetDirectoryName(backend.ExecutablePath)!, "contributions"),
                    ct => BundledModuleSetup.StopAsync(backend, services.GetService<GatewayProcessManager>(), ct),
                    (root, modules, ct) => BundledModuleSetup.ConfigureAsync(frontend, root, modules, true, ct), token);
                committed = true;
            }, deadline.Token);
            if (!committed) throw new InvalidOperationException("Installation was suppressed.");
            _connectionGate.Release();
            connectionClaimed = false;
            candidate.Dispose();
            _moduleCandidate = null;
            ModuleConfirmButton.Visibility = Visibility.Collapsed;
            ModuleGitHubToken.Password = string.Empty;
            ModuleInstallStatus.Text = "Installed. Starting a new Runtime graph through the normal module loader.";
            _retryCts?.Dispose();
            _retryCts = new CancellationTokenSource();
            await RunConnectionFlowAsync(null, _retryCts.Token);
        }
        catch (Exception)
        {
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
    }
}
