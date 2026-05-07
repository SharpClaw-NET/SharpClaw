using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// SharpClaw connection options tool window.
/// </summary>
[VisualStudioContribution]
internal sealed class SharpClawOptionsToolWindow : ToolWindow
{
    private readonly SharpClawConnectionOptionsStore _optionsStore;
    private readonly SharpClawConnector _connector;
    private readonly SharpClawOutputLog _log;

    public SharpClawOptionsToolWindow(
        SharpClawConnectionOptionsStore optionsStore,
        SharpClawConnector connector,
        SharpClawOutputLog log)
    {
        _optionsStore = optionsStore;
        _connector = connector;
        _log = log;
        Title = "SharpClaw Options";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        DockDirection = Dock.Right,
    };

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        var control = new SharpClawOptionsControl(_optionsStore, _connector, _log);
        return Task.FromResult<IRemoteUserControl>(control);
    }
}
