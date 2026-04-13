using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SharpClaw.VS2026Extension;

/// <summary>
/// SharpClaw package for Visual Studio 2026. Manages WebSocket connection to
/// SharpClaw backend and handles editor action requests from AI agents.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class SharpClawPackage : AsyncPackage
{
    public const string PackageGuidString = "d5e3a8f1-4c2b-4e9d-8f1a-2b3c4d5e6f7a";

    private SharpClawBridgeClient? _bridgeClient;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        await ConnectCommand.InitializeAsync(this);
        await DisconnectCommand.InitializeAsync(this);

        // Auto-connect on startup if backend is running
        await TryAutoConnectAsync();
    }

    private async Task TryAutoConnectAsync()
    {
        try
        {
            await ConnectAsync();
        }
        catch
        {
            // Silent fail on auto-connect; user can manually connect via Tools menu
        }
    }

    public async Task ConnectAsync()
    {
        if (_bridgeClient?.IsConnected == true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var uiShell = await GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell)) 
                as Microsoft.VisualStudio.Shell.Interop.IVsUIShell;
            // Already connected
            return;
        }

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solution = dte?.Solution;
            string? workspacePath = solution?.FullName;

            if (string.IsNullOrEmpty(workspacePath))
            {
                workspacePath = Environment.CurrentDirectory;
            }

            _bridgeClient = new SharpClawBridgeClient(workspacePath, this);
            await _bridgeClient.ConnectAsync();

            // Status message would go here
        }
        catch
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Error message would go here
            _bridgeClient = null;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_bridgeClient == null)
        {
            return;
        }

        await _bridgeClient.DisconnectAsync();
        _bridgeClient = null;
        // Status message would go here
    }

    public bool IsConnected => _bridgeClient?.IsConnected == true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            JoinableTaskFactory.Run(async () =>
            {
                if (_bridgeClient != null)
                {
                    await _bridgeClient.DisconnectAsync();
                }
            });
            _bridgeClient = null;
        }
        base.Dispose(disposing);
    }
}
