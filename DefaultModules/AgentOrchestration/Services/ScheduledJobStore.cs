using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Models;
using SharpClaw.Modules.AgentOrchestration.ScheduledJobs;

namespace SharpClaw.Modules.AgentOrchestration.Services;

public sealed class ScheduledJobStore(IModuleConfigStore configStore)
{
    private const string StoreKey = "scheduled_jobs.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScheduledJobDB> CreateAsync(
        ScheduledJobDB job,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var jobs = await LoadUnlockedAsync(ct);
            var now = DateTimeOffset.UtcNow;
            if (job.Id == Guid.Empty)
                job.Id = Guid.NewGuid();
            if (job.CreatedAt == default)
                job.CreatedAt = now;
            job.UpdatedAt = now;
            jobs.Add(job);
            await SaveUnlockedAsync(jobs, ct);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScheduledJobDB?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await LoadUnlockedAsync(ct)).FirstOrDefault(job => job.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledJobDB>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. await LoadUnlockedAsync(ct)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledJobDB>> ListDueAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. (await LoadUnlockedAsync(ct))
                .Where(job => job.Status == ScheduledTaskStatus.Pending && job.NextRunAt <= now)
                .OrderBy(job => job.NextRunAt)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScheduledJobDB?> UpdateAsync(
        Guid id,
        Action<ScheduledJobDB> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(ct);
        try
        {
            var jobs = await LoadUnlockedAsync(ct);
            var job = jobs.FirstOrDefault(candidate => candidate.Id == id);
            if (job is null)
                return null;

            update(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(jobs, ct);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var jobs = await LoadUnlockedAsync(ct);
            var removed = jobs.RemoveAll(job => job.Id == id) > 0;
            if (removed)
                await SaveUnlockedAsync(jobs, ct);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ScheduledJobDB>> LoadUnlockedAsync(CancellationToken ct)
    {
        var json = await configStore.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<ScheduledJobDB>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnlockedAsync(List<ScheduledJobDB> jobs, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(jobs, JsonOptions);
        await configStore.SetAsync(StoreKey, json, ct);
    }
}
