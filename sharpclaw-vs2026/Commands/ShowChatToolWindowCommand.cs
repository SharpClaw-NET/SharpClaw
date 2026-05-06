using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using SharpClaw.VS2026Extension.Services;
using SharpClaw.VS2026Extension.ToolWindows;

namespace SharpClaw.VS2026Extension.Commands;

/// <summary>
/// Command that opens the SharpClaw chat tool window from the
/// <c>View → Other Windows</c> and <c>Extensions</c> menus.
/// </summary>
[VisualStudioContribution]
internal sealed class ShowChatToolWindowCommand : Command
{
    private readonly SharpClawOutputLog _log;

    public ShowChatToolWindowCommand(SharpClawOutputLog log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SharpClaw.ShowChatToolWindowCommand.DisplayName%")
    {
        Placements =
        [
            CommandPlacement.KnownPlacements.ExtensionsMenu,
            CommandPlacement.KnownPlacements.ViewOtherWindowsMenu,
        ],
        Icon = new(ImageMoniker.KnownValues.Comment, IconSettings.IconAndText),
    };

    /// <inheritdoc />
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _log.EnsureInitializedAsync(Extensibility, cancellationToken).ConfigureAwait(false);
        await base.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        => Extensibility.Shell().ShowToolWindowAsync<SharpClawChatToolWindow>(activate: true, cancellationToken);
}
