using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.NetworkTriggers;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> implementations
/// for the network trigger-attribute family:
/// <c>[OnHostReachable]</c>, <c>[OnHostUnreachable]</c>,
/// <c>[OnNetworkChanged]</c>. Behavior preserved verbatim from the legacy
/// core parser switch.
/// </summary>
internal static class NetworkTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnHostReachable"]   = new HostHandler(NetworkTriggerKeys.HostReachable),
            ["OnHostUnreachable"] = new HostHandler(NetworkTriggerKeys.HostUnreachable),
            ["OnNetworkChanged"]  = new NetworkChangedHandler(),
        };

    private sealed class HostHandler(string triggerKey) : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var host = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(host))
                p[NetworkTriggerKeys.HostName] = host;
            var port = context.GetNamedIntArg("Port");
            if (port.HasValue)
                p[NetworkTriggerKeys.HostPort] = port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new TaskTriggerDefinition
            {
                TriggerKey = triggerKey,
                Parameters = p,
            };
        }
    }

    private sealed class NetworkChangedHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var ssid = context.GetNamedStringArg("Ssid");
            if (!string.IsNullOrEmpty(ssid))
                p[NetworkTriggerKeys.NetworkSsid] = ssid;
            var state = context.GetNamedEnumArg<NetworkState>("State") ?? NetworkState.Any;
            if (state != default)
                p[NetworkTriggerKeys.NetworkState] = state.ToString();
            return new TaskTriggerDefinition
            {
                TriggerKey = NetworkTriggerKeys.NetworkChanged,
                Parameters = p,
            };
        }
    }
}
