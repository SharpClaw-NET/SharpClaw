using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.AgentOrchestration.Models;

namespace SharpClaw.Modules.AgentOrchestration.Services;

public sealed class SkillStore(IModuleConfigStore configStore)
{
    private const string StoreKey = "skills.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SkillDB> CreateAsync(SkillDB skill, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var skills = await LoadUnlockedAsync(ct);
            var now = DateTimeOffset.UtcNow;
            if (skill.Id == Guid.Empty)
                skill.Id = Guid.NewGuid();
            if (skill.CreatedAt == default)
                skill.CreatedAt = now;
            skill.UpdatedAt = now;
            skills.Add(skill);
            await SaveUnlockedAsync(skills, ct);
            return skill;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SkillDB?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await LoadUnlockedAsync(ct)).FirstOrDefault(skill => skill.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SkillDB>> ListAsync(CancellationToken ct = default)
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

    public async Task<SkillDB?> UpdateAsync(
        Guid id,
        Action<SkillDB> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(ct);
        try
        {
            var skills = await LoadUnlockedAsync(ct);
            var skill = skills.FirstOrDefault(candidate => candidate.Id == id);
            if (skill is null)
                return null;

            update(skill);
            skill.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(skills, ct);
            return skill;
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
            var skills = await LoadUnlockedAsync(ct);
            var removed = skills.RemoveAll(skill => skill.Id == id) > 0;
            if (removed)
                await SaveUnlockedAsync(skills, ct);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<SkillDB>> LoadUnlockedAsync(CancellationToken ct)
    {
        var json = await configStore.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<SkillDB>>(json, JsonOptions) ?? [];
    }

    private async Task SaveUnlockedAsync(List<SkillDB> skills, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(skills, JsonOptions);
        await configStore.SetAsync(StoreKey, json, ct);
    }
}
