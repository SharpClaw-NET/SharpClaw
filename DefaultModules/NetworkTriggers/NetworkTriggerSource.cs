using System.Net.NetworkInformation;

using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.NetworkTriggers;

/// <summary>
/// Fires when network availability changes, matching the optional SSID and
/// <see cref="NetworkState"/> declared on the binding.
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class NetworkTriggerSource(
    ILogger<NetworkTriggerSource> logger) : ITaskTriggerSource, IDisposable
{
    private IReadOnlyList<ITaskTriggerSourceContext> _contexts = [];
    private bool _subscribed;

    public string TriggerKey => NetworkTriggerKeys.NetworkChanged;

    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _contexts = contexts;

        if (!_subscribed)
        {
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_subscribed)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _subscribed = false;
        }

        _contexts = [];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def) =>
        def.TriggerKey == NetworkTriggerKeys.NetworkChanged
            ? def.Parameters.GetValueOrDefault(NetworkTriggerKeys.NetworkSsid)
            : null;

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var connected = e.IsAvailable;
        _ = Task.Run(async () =>
        {
            foreach (var ctx in _contexts)
            {
                var requiredState = ParseState(ctx.Definition.Parameters.GetValueOrDefault(NetworkTriggerKeys.NetworkState));

                if (requiredState == NetworkState.Connected    && !connected) continue;
                if (requiredState == NetworkState.Disconnected && connected)  continue;

                // SSID filtering is platform-specific; skip if not checkable
                var ssid = ctx.Definition.Parameters.GetValueOrDefault(NetworkTriggerKeys.NetworkSsid);
                if (!string.IsNullOrWhiteSpace(ssid) && !TryMatchSsid(ssid))
                    continue;

                try { await ctx.FireAsync(); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "NetworkTriggerSource failed to fire context for definition {Id}.", ctx.TaskDefinitionId);
                }
            }
        });
    }

    /// <summary>SSID matching is best-effort; returns true when the check cannot be performed.</summary>
    private static bool TryMatchSsid(string expectedSsid)
    {
        // Full SSID detection requires platform-specific APIs not available cross-platform.
        // Return true so the trigger is not silently suppressed on unsupported platforms.
        return true;
    }

    private static NetworkState ParseState(string? value) =>
        Enum.TryParse<NetworkState>(value, ignoreCase: true, out var parsed) ? parsed : default;
}
