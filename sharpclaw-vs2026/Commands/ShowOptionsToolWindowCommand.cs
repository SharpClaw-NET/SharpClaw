using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using SharpClaw.VS2026Extension.Services;
using SharpClaw.VS2026Extension.ToolWindows;

namespace SharpClaw.VS2026Extension.Commands;

/// <summary>
/// Opens the SharpClaw connection options tool window.
/// </summary>
[VisualStudioContribution]
internal sealed class ShowOptionsToolWindowCommand : Command
{
    private readonly SharpClawOutputLog _log;

    public ShowOptionsToolWindowCommand(SharpClawOutputLog log)
    {
        _log = log;
    }

    public override CommandConfiguration CommandConfiguration => new("%SharpClaw.ShowOptionsToolWindowCommand.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Settings, IconSettings.IconAndText),
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _log.EnsureInitializedAsync(Extensibility, cancellationToken).ConfigureAwait(false);
        await base.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        => Extensibility.Shell().ShowToolWindowAsync<SharpClawOptionsToolWindow>(
            activate: true,
            cancellationToken: cancellationToken);
}
