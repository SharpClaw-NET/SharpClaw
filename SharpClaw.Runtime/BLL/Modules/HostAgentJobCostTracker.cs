using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Runtime.BLL.Modules;

public sealed class HostAgentJobCostTracker(
    IServiceScopeFactory scopeFactory) : IAgentJobCostTracker
{
    public async Task RecordTokensAsync(
        Guid jobId,
        int promptTokens,
        int completionTokens,
        CancellationToken ct = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Job id is required.", nameof(jobId));
        if (promptTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(promptTokens), promptTokens,
                "Prompt tokens cannot be negative.");
        if (completionTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(completionTokens), completionTokens,
                "Completion tokens cannot be negative.");
        if (promptTokens == 0 && completionTokens == 0) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<AgentJobService>();
        await jobs.RecordTokensAsync([jobId], promptTokens, completionTokens, ct);
    }
}
