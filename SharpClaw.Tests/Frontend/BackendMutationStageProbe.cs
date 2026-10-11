using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

internal sealed class BackendMutationStageProbe(string operation, BundledBackendMutationStage stage, Exception? failure = null)
{
    private readonly ControlledOperationGate _gate = new(failure);
    private CancellationToken _heldToken;

    internal int Calls => _gate.Calls;
    internal bool IsHeldTokenCancelled => _heldToken.IsCancellationRequested;
    internal Task WaitForEntryAsync() => _gate.WaitForEntryAsync();
    internal void Release() => _gate.Release();

    internal Task ObserveAsync(string observedOperation, BundledBackendMutationStage observedStage, CancellationToken token)
    {
        if (!string.Equals(operation, observedOperation, StringComparison.Ordinal) || stage != observedStage)
            return Task.CompletedTask;
        _heldToken = token;
        // This controlled physical stage deliberately settles only when released, even after its receipt is canceled.
        return _gate.RunAsync().AsTask();
    }
}
