using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.Commands;

/// <summary>
/// Drops the cached <see cref="SharpClawHttpClient"/> so the next backend
/// call rediscovers the running SharpClaw process. Mirrors the legacy
/// <c>Tools &gt; SharpClaw &gt; Disconnect</c> command.
/// </summary>
[VisualStudioContribution]
internal sealed class DisconnectCommand : Command
{
    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;

    public DisconnectCommand(SharpClawBackend backend, SharpClawOutputLog log)
    {
        _backend = backend;
        _log = log;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SharpClaw.DisconnectCommand.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Unlink, IconSettings.IconAndText),
    };

    /// <inheritdoc />
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _log.EnsureInitializedAsync(Extensibility, cancellationToken).ConfigureAwait(false);
        await base.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        _backend.Reset();
        await _log.WriteLineAsync("Disconnected from SharpClaw backend.").ConfigureAwait(false);
    }
}
