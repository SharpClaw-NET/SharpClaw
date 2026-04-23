using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SharpClaw.VS2026Extension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(SharpClawOptionsPage),
    "SharpClaw", "General", 0, 0, true,
    DescriptionResourceID = 0)]
public sealed class SharpClawPackage : AsyncPackage
{
    public const string PackageGuidString = "d5e3a8f1-4c2b-4e9d-8f1a-2b3c4d5e6f7a";

    private static readonly Guid OutputPaneGuid =
        new("b7e4f2a1-6d3c-4e8b-9a1f-5c2d7e8f9a0b");

    private SharpClawBridgeClient? _bridgeClient;
    private IVsOutputWindowPane? _outputPane;

    /// <summary>
    /// The user-configurable options under <c>Tools &gt; Options &gt; SharpClaw</c>.
    /// </summary>
    public SharpClawOptionsPage Options
        => (SharpClawOptionsPage)GetDialogPage(typeof(SharpClawOptionsPage));

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var commandService = await GetServiceAsync(typeof(IMenuCommandService))
            as OleMenuCommandService;

        if (commandService != null)
        {
            await ConnectCommand.InitializeAsync(this, commandService);
            await DisconnectCommand.InitializeAsync(this, commandService);
        }

        var options = Options;

        if (!options.AutoConnect)
        {
            await WriteOutputAsync("Auto-connect disabled in settings.");
            return;
        }

        // Auto-connect in the background so InitializeAsync returns immediately
        // and VS can finish loading without blocking on network I/O.
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            // Let VS finish its startup sequence before we touch DTE / network.
            var delay = Math.Max(0, options.AutoConnectDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);

            try
            {
                var timeout = Math.Max(1, options.ConnectionTimeoutSeconds);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                await ConnectAsync(cts.Token);
                await WriteOutputAsync("Auto-connected to SharpClaw backend.");
            }
            catch (OperationCanceledException)
            {
                await WriteOutputAsync("Auto-connect timed out — backend not reachable. Use Tools > SharpClaw > Connect.");
            }
            catch
            {
                await WriteOutputAsync("Auto-connect skipped — backend not reachable. Use Tools > SharpClaw > Connect.");
            }
        });
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_bridgeClient?.IsConnected == true)
            return;

        await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        var workspacePath = dte?.Solution?.FullName;

        if (string.IsNullOrEmpty(workspacePath))
            workspacePath = Environment.CurrentDirectory;

        var options = Options;
        var config = new BridgeClientConfig(
            options.BridgeUri,
            options.ResolvedApiKeyFilePath,
            options.BackendInstanceId,
            options.ConnectionTimeoutSeconds);

        _bridgeClient = new SharpClawBridgeClient(workspacePath, this, config);
        await _bridgeClient.ConnectAsync(ct);
    }

    public async Task DisconnectAsync()
    {
        if (_bridgeClient == null)
            return;

        await _bridgeClient.DisconnectAsync();
        _bridgeClient = null;
    }

    public bool IsConnected => _bridgeClient?.IsConnected == true;

    // ═══════════════════════════════════════════════════════════════
    // UI feedback helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the VS status bar text (bottom of the IDE window).
    /// </summary>
    public async Task SetStatusBarTextAsync(string text)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(text);
    }

    /// <summary>
    /// Writes a timestamped line to the "SharpClaw" Output Window pane.
    /// Creates the pane on first use.
    /// </summary>
    public async Task WriteOutputAsync(string message)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();

        if (_outputPane == null)
        {
            var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow))
                as IVsOutputWindow;

            if (outputWindow != null)
            {
                var guid = OutputPaneGuid;
                outputWindow.CreatePane(ref guid, "SharpClaw", 1, 0);
                outputWindow.GetPane(ref guid, out _outputPane);
            }
        }

        _outputPane?.OutputStringThreadSafe(
            $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    // ═══════════════════════════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (disposing && _bridgeClient != null)
        {
            JoinableTaskFactory.Run(async () =>
                await _bridgeClient.DisconnectAsync());
            _bridgeClient = null;
        }
        base.Dispose(disposing);
    }
}
