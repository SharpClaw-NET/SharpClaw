using Microsoft.VisualStudio.Extensibility.UI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// Remote UI control hosting the SharpClaw chat experience inside the
/// Visual Studio tool window. The XAML resource <c>SharpClawChatControl.xaml</c>
/// is embedded next to this type and discovered by name.
/// </summary>
internal sealed class SharpClawChatControl : RemoteUserControl
{
    public SharpClawChatViewModel ViewModel { get; }

    public SharpClawChatControl(SharpClawBackend backend, SharpClawOutputLog log)
        : this(new SharpClawChatViewModel(backend, log)) { }

    private SharpClawChatControl(SharpClawChatViewModel vm) : base(vm)
    {
        ViewModel = vm;
    }
}
