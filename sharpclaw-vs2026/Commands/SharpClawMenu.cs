using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace SharpClaw.VS2026Extension.Commands;

/// <summary>
/// Declares the <c>Tools &gt; SharpClaw</c> submenu that hosts the Chat,
/// Connect, and Disconnect commands. Replaces the legacy in-process
/// <c>SharpClawPackage.vsct</c> menu definition.
/// </summary>
internal static class SharpClawMenu
{
    [VisualStudioContribution]
    public static MenuConfiguration ToolsSharpClawMenu => new("%SharpClaw.ToolsMenu.DisplayName%")
    {
        Children =
        [
            MenuChild.Command<ShowChatToolWindowCommand>(),
            MenuChild.Command<ShowOptionsToolWindowCommand>(),
            MenuChild.Separator,
            MenuChild.Command<RefreshChatCommand>(),
            MenuChild.Command<ConnectCommand>(),
            MenuChild.Command<DisconnectCommand>(),
        ],
    };

    [VisualStudioContribution]
    public static CommandGroupConfiguration ToolsSharpClawGroup => new(GroupPlacement.KnownPlacements.ToolsMenu)
    {
        Children =
        [
            GroupChild.Menu(ToolsSharpClawMenu),
        ],
    };
}
