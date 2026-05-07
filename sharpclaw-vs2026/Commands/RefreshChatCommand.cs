using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.Commands;

[VisualStudioContribution]
internal sealed class RefreshChatCommand : Command
{
    private readonly SharpClawChatSession _chatSession;
    private readonly SharpClawOutputLog _log;

    public RefreshChatCommand(SharpClawChatSession chatSession, SharpClawOutputLog log)
    {
        _chatSession = chatSession;
        _log = log;
    }

    public override CommandConfiguration CommandConfiguration => new("%SharpClaw.RefreshChatCommand.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _log.EnsureInitializedAsync(Extensibility, cancellationToken).ConfigureAwait(false);
        await base.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _log.WriteLineAsync("Tools menu Refresh requested.").ConfigureAwait(false);
        if (!await _chatSession.RefreshActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            await _log.WriteLineAsync("Refresh skipped: SharpClaw Chat tool window is not open.").ConfigureAwait(false);
        }
    }
}
