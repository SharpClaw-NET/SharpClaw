using System.Text.Json;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.AgentOrchestration.Services;

/// <summary>
/// Wraps agent lifecycle, task editing, and skill access DB operations
/// for the Agent Orchestration module.
/// </summary>
internal sealed class AgentOrchestrationService(
    ScheduledJobStore scheduledJobs,
    SkillStore skills,
    IAgentManager agentManager)
{
    // ═══════════════════════════════════════════════════════════════
    // CREATE SUB-AGENT
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> CreateSubAgentAsync(
        JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.TryGetProperty("name", out var nProp)
            ? nProp.GetString() : null;
        var modelIdStr = parameters.TryGetProperty("modelId", out var mProp)
            ? mProp.GetString() : null;
        var systemPrompt = parameters.TryGetProperty("systemPrompt", out var sProp)
            ? sProp.GetString() : null;

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "create_sub_agent requires a 'name' parameter.");

        if (!Guid.TryParse(modelIdStr, out var modelId))
            throw new InvalidOperationException(
                "create_sub_agent requires a valid 'modelId' GUID.");

        var (agentId, modelName, agentName) =
            await agentManager.CreateSubAgentAsync(name, modelId, systemPrompt, ct);

        return $"Created sub-agent '{agentName}' (id={agentId}, model={modelName}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // MANAGE AGENT
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ManageAgentAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        string? newName = parameters.TryGetProperty("name", out var nameProp)
            && nameProp.GetString() is { Length: > 0 } n ? n : null;

        string? systemPrompt = parameters.TryGetProperty("systemPrompt", out var spProp)
            ? spProp.GetString() : null;

        Guid? newModelId = parameters.TryGetProperty("modelId", out var midProp)
            && Guid.TryParse(midProp.GetString(), out var mid) ? mid : null;

        return await agentManager.UpdateAgentAsync(resourceId, newName, systemPrompt, newModelId, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // EDIT TASK
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> EditTaskAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        var changes = new List<string>();
        var task = await scheduledJobs.UpdateAsync(resourceId, storedTask =>
        {
            if (parameters.TryGetProperty("name", out var nameProp)
                && nameProp.GetString() is { Length: > 0 } newName)
            {
                storedTask.Name = newName;
                changes.Add($"name='{newName}'");
            }

            if (parameters.TryGetProperty("repeatIntervalMinutes", out var intervalProp)
                && intervalProp.TryGetInt32(out var intervalMinutes))
            {
                storedTask.RepeatInterval = intervalMinutes > 0
                    ? TimeSpan.FromMinutes(intervalMinutes)
                    : null;
                changes.Add($"repeatInterval={storedTask.RepeatInterval?.ToString() ?? "none"}");
            }

            if (parameters.TryGetProperty("maxRetries", out var retriesProp)
                && retriesProp.TryGetInt32(out var retries))
            {
                storedTask.MaxRetries = retries;
                changes.Add($"maxRetries={retries}");
            }
        }, ct)
            ?? throw new InvalidOperationException(
                $"ScheduledJob {resourceId} not found.");

        if (changes.Count == 0)
            return $"Task '{task.Name}' (id={task.Id}) — no changes applied.";

        var summary = string.Join(", ", changes);
        return $"Updated task '{task.Name}' (id={task.Id}): {summary}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // ACCESS SKILL
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> AccessSkillAsync(
        Guid resourceId, CancellationToken ct)
    {
        var skill = await skills.GetByIdAsync(resourceId, ct)
            ?? throw new InvalidOperationException(
                $"Skill {resourceId} not found.");

        return $"Skill: {skill.Name}\n\n{skill.SkillText}";
    }

    // ═══════════════════════════════════════════════════════════════
    // EDIT AGENT HEADER
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> EditAgentHeaderAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("header", out var hProp))
            return $"No changes applied to agent header (id={resourceId})."
                + " Pass \"header\": \"<text>\" to set or \"header\": \"\" to clear.";

        var header = hProp.GetString();

        await agentManager.SetAgentHeaderAsync(resourceId, header, ct);

        return string.IsNullOrEmpty(header)
            ? $"Cleared custom chat header for agent (id={resourceId})."
            : $"Updated custom chat header for agent (id={resourceId}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // EDIT CHANNEL HEADER
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> EditChannelHeaderAsync(
        Guid resourceId, JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("header", out var hProp))
            return $"No changes applied to channel header (id={resourceId})."
                + " Pass \"header\": \"<text>\" to set or \"header\": \"\" to clear.";

        var header = hProp.GetString();

        await agentManager.SetChannelHeaderAsync(resourceId, header, ct);

        return string.IsNullOrEmpty(header)
            ? $"Cleared custom chat header for channel (id={resourceId})."
            : $"Updated custom chat header for channel (id={resourceId}).";
    }
}
