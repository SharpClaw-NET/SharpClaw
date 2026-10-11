using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

internal sealed class ReadinessPhaseProbe : ClientActionServiceSet.IClientActionContextSink
{
    private int _pingDispatches;
    internal TaskCompletionSource SecondPingDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource StoppedReceipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Observe(ActionContext<KernelActionEnvelope> context)
    {
        if (context.ActionKey == ClientActionCatalog.CommandDispatch &&
            context.Action.Payload is ClientCommandInvocation command &&
            string.Equals(command.Operation, "http.send", StringComparison.Ordinal) &&
            string.Equals(command.Method, "GET", StringComparison.Ordinal) &&
            string.Equals(command.Path, "/ping", StringComparison.Ordinal) &&
            Interlocked.Increment(ref _pingDispatches) == 2)
            SecondPingDispatch.TrySetResult();
        if ((context.ActionKey == ClientActionCatalog.CommandFail || context.ActionKey == ClientActionCatalog.CommandCancel) &&
            context.Action.Payload is ClientCommandSignal signal && string.Equals(signal.Operation, "http.send", StringComparison.Ordinal))
            StoppedReceipt.TrySetResult();
    }
}
