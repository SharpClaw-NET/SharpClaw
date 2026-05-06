using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// Remote UI control hosting the SharpClaw chat experience inside the
/// Visual Studio tool window. The XAML resource <c>SharpClawChatControl.xaml</c>
/// is embedded next to this type and discovered by name.
///
/// <para>The control owns a sticky <see cref="NonConcurrentSynchronizationContext"/>
/// that is passed to both <see cref="RemoteUserControl"/> (so async commands and
/// data-binding-driven property setters all run on it) and to the view-model
/// (so background work — periodic refresh, SSE watch, reconnect handler — can
/// post collection / property mutations onto the same context). This makes
/// every VM mutation strictly serialized, eliminating the
/// <c>ObservableCollection</c> concurrent-modification crashes that brought
/// Visual Studio down when the channel selector cascaded into a thread reload
/// while a periodic refresh / watch event was still in flight.</para>
/// </summary>
internal sealed class SharpClawChatControl : RemoteUserControl
{
    public SharpClawChatViewModel ViewModel { get; }

    public SharpClawChatControl(SharpClawBackend backend, SharpClawOutputLog log)
        : this(new NonConcurrentSynchronizationContext(sticky: true), backend, log) { }

    private SharpClawChatControl(NonConcurrentSynchronizationContext ctx, SharpClawBackend backend, SharpClawOutputLog log)
        : this(ctx, new SharpClawChatViewModel(backend, log, ctx)) { }

    private SharpClawChatControl(SynchronizationContext ctx, SharpClawChatViewModel vm)
        : base(vm, ctx)
    {
        ViewModel = vm;
    }
}
