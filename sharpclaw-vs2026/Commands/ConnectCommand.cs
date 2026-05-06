using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.Commands;

/// <summary>
/// Forces a fresh discovery of the SharpClaw backend and verifies that the
/// API is reachable. Mirrors the legacy <c>Tools &gt; SharpClaw &gt; Connect</c>
/// command from the in-process VSPackage.
/// </summary>
[VisualStudioContribution]
internal sealed class ConnectCommand : Command
{
    private readonly SharpClawConnector _connector;
    private readonly SharpClawOutputLog _log;

    public ConnectCommand(SharpClawConnector connector, SharpClawOutputLog log)
    {
        _connector = connector;
        _log = log;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SharpClaw.ConnectCommand.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Link, IconSettings.IconAndText),
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
        try
        {
            await _connector.ConnectAsync("Tools menu", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _log.WriteLineAsync($"Connect threw: {ex}").ConfigureAwait(false);
        }
    }
}
