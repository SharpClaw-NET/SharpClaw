using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// SharpClaw chat tool window. Hosts a Remote UI control with the
/// Context → Channel → Thread selection flow and a streaming chat composer.
/// </summary>
[VisualStudioContribution]
internal sealed class SharpClawChatToolWindow : ToolWindow
{
    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;
    private readonly SharpClawChatSession _chatSession;

    public SharpClawChatToolWindow(SharpClawBackend backend, SharpClawOutputLog log, SharpClawChatSession chatSession)
    {
        _backend = backend;
        _log = log;
        _chatSession = chatSession;
        Title = "SharpClaw Chat";
    }

    /// <inheritdoc />
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        DockDirection = Dock.Right,
    };

    /// <inheritdoc />
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        var control = new SharpClawChatControl(_backend, _log);
        _chatSession.Register(control.ViewModel);
        // Kick off the initial load + periodic refresh so newly created
        // contexts/channels/threads in SharpClaw show up without requiring a
        // manual click on Refresh.
        control.ViewModel.EnsureStarted();
        return Task.FromResult<IRemoteUserControl>(control);
    }
}
