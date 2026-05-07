using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// Remote UI control for SharpClaw connection options.
/// </summary>
internal sealed class SharpClawOptionsControl : RemoteUserControl
{
    public SharpClawOptionsViewModel ViewModel { get; }

    public SharpClawOptionsControl(
        SharpClawConnectionOptionsStore optionsStore,
        SharpClawConnector connector,
        SharpClawOutputLog log)
        : this(new NonConcurrentSynchronizationContext(sticky: true), optionsStore, connector, log) { }

    private SharpClawOptionsControl(
        NonConcurrentSynchronizationContext ctx,
        SharpClawConnectionOptionsStore optionsStore,
        SharpClawConnector connector,
        SharpClawOutputLog log)
        : this(ctx, new SharpClawOptionsViewModel(optionsStore, connector, log, ctx)) { }

    private SharpClawOptionsControl(SynchronizationContext ctx, SharpClawOptionsViewModel vm)
        : base(vm, ctx)
    {
        ViewModel = vm;
    }
}
