using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

/// <summary>
/// Covers finding <c>L-001</c> from
/// <c>docs/internal/llamasharp-pipeline-audit-and-remediation-plan.md</c>:
/// <see cref="LocalInferenceApiClient.CurrentModelId"/> must be isolated
/// per async flow so concurrent chat requests against the singleton
/// provider client cannot overwrite each other's model ID.
/// </summary>
[TestFixture]
public class LocalInferenceApiClientCurrentModelIdTests
{
    [Test]
    public async Task ConcurrentRequestsOnDifferentModelsDoNotRaceAsync()
    {
        var client = new LocalInferenceApiClient(new LocalInferenceProcessManager());

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        using var startGate = new SemaphoreSlim(0, 2);
        using var assertGate = new SemaphoreSlim(0, 2);

        async Task<Guid> RunAsync(Guid id)
        {
            client.CurrentModelId = id;
            startGate.Release();
            // Wait until the other task has also set its CurrentModelId
            // before reading ours back. If the field were shared mutable
            // state on the singleton, the read would return whichever
            // value was written last.
            await assertGate.WaitAsync();
            return client.CurrentModelId;
        }

        var taskA = Task.Run(() => RunAsync(idA));
        var taskB = Task.Run(() => RunAsync(idB));

        await startGate.WaitAsync();
        await startGate.WaitAsync();
        assertGate.Release();
        assertGate.Release();

        var observedA = await taskA;
        var observedB = await taskB;

        observedA.Should().Be(idA);
        observedB.Should().Be(idB);
    }

    [Test]
    public void DefaultValueIsEmptyGuid()
    {
        var client = new LocalInferenceApiClient(new LocalInferenceProcessManager());

        client.CurrentModelId.Should().Be(Guid.Empty);
    }
}
