using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Runs the verbose connect routine once at extension load so the SharpClaw
/// Output pane is populated and the dropdown entry appears without requiring
/// a manual <c>Tools &gt; SharpClaw &gt; Connect</c> click first.
///
/// Auto-connect runs on a background task so a slow or unreachable backend
/// never blocks the host's initialization sequence.
/// </summary>
internal sealed class SharpClawAutoConnectInitializer : IExtensionInitializer
{
    private readonly SharpClawConnector _connector;
    private readonly SharpClawOutputLog _log;

    public SharpClawAutoConnectInitializer(SharpClawConnector connector, SharpClawOutputLog log)
    {
        _connector = connector;
        _log = log;
    }

    public Task InitializeAsync(
        ExtensionCore extension,
        IServiceProvider serviceProvider,
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunAutoConnectLoopAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Auto-connect retry loop. The extension typically loads before the
    /// SharpClaw backend is alive (especially when VS launches at logon and
    /// SharpClaw is started manually), so a single attempt at startup is
    /// rarely enough. We retry on a backoff schedule until either:
    ///   - a Connect attempt succeeds (after which the loop ends â€” further
    ///     reconnects are user-driven via Tools &gt; SharpClaw &gt; Connect), or
    ///   - VS shuts down (cancellation).
    /// Each attempt is fully narrated by <see cref="SharpClawConnector"/>, so
    /// the SharpClaw output pane shows the discovery + probe trace every
    /// time without us duplicating logging here.
    /// </summary>
    private async Task RunAutoConnectLoopAsync(CancellationToken ct)
    {
        // Schedule: a fast first attempt, then quick retries during the
        // first ~minute (covers "I just clicked SharpClaw.exe"), then slow
        // background polling for the rest of the session.
        var schedule = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
        };
        var slowInterval = TimeSpan.FromMinutes(1);

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var trigger = attempt == 0
                    ? "auto-connect at startup"
                    : $"auto-connect retry #{attempt}";
                var result = await _connector.ConnectAsync(trigger, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    await _log.WriteLineAsync(
                        "Auto-connect succeeded â€” further reconnects must be user-initiated.")
                        .ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await _log.WriteLineAsync($"Auto-connect attempt threw: {ex.Message}").ConfigureAwait(false);
            }

            var delay = attempt < schedule.Length ? schedule[attempt] : slowInterval;
            attempt++;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
