using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.DefaultPackages.TestHarness;


internal sealed class TestHarnessCostFeed(string providerKey, TestHarnessState state) : IProviderCostFeed
{
    public async Task<ProviderCostResult?> GetCostsAsync(
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        CancellationToken ct = default)
    {
        var sequence = state.NextSequence();
        var sw = Stopwatch.StartNew();
        try
        {
            var behavior = state.CostBehavior;
            if (behavior.LatencyMs > 0)
                await Task.Delay(behavior.LatencyMs, ct).ConfigureAwait(false);

            return behavior.PermissionDenied ? null : behavior.Result;
        }
        finally
        {
            sw.Stop();
            var behavior = state.CostBehavior;
            state.RecordCostCall(new CapturedCostCall(
                sequence,
                providerKey,
                startTime,
                endTime,
                sw.ElapsedMilliseconds,
                behavior.PermissionDenied));
        }
    }
}
