using System.Text.Json;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Preserves typed Runtime event payloads across Core object snapshots.</summary>
internal sealed class RuntimeEventActionResultSnapshotter : IKernelActionResultSnapshotter
{
    private readonly JsonKernelActionResultSnapshotter _fallback = new();

    public TResult Snapshot<TResult>(TResult result)
    {
        if (result is RuntimeEventActionInvocation invocation)
        {
            return (TResult)(object)(invocation with
            {
                Payload = SnapshotPayload(invocation.Payload),
            });
        }

        if (result is RuntimeEventPayload or RuntimeEventOutboxTransition)
            return result;

        return _fallback.Snapshot(result);
    }

    private static object? SnapshotPayload(object? payload) => payload switch
    {
        RuntimeEventPayload eventPayload => eventPayload,
        RuntimeEventOutboxTransition transition => transition,
        JsonElement json => json.Clone(),
        _ => payload,
    };
}
