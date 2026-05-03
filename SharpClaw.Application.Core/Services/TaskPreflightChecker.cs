using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Evaluates task environment requirements before an instance is created.
/// Checks are divided into two tiers:
/// <list type="bullet">
///   <item><see cref="CheckStatic"/> — platform checks only, no DB access.</item>
///   <item><see cref="CheckRuntimeAsync"/> — full DB-backed checks performed at instance-creation time.</item>
/// </list>
/// </summary>
public sealed class TaskPreflightChecker(
    SharpClawDbContext db,
    ModuleRegistry moduleRegistry)
{
    // ═══════════════════════════════════════════════════════════════
    // Static check (no DB access)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Run only static platform checks. Called at definition registration time
    /// so platform mismatches are surfaced without DB access.
    /// </summary>
    public TaskPreflightResult CheckStatic(IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        var findings = new List<TaskPreflightFinding>();

        foreach (var req in requirements)
        {
            if (req.Kind != TaskRequirementKind.RequiresPlatform)
                continue;

            if (!Enum.TryParse<TaskPlatform>(req.Value, ignoreCase: false, out var platform))
            {
                // Invalid name — already caught by TASK401 at validation time; skip silently.
                continue;
            }

            var passed = IsPlatformSatisfied(platform);
            findings.Add(new TaskPreflightFinding(
                req.Kind.ToString(),
                req.Severity,
                passed,
                passed
                    ? $"Platform '{req.Value}' is satisfied on the current host."
                    : $"Platform '{req.Value}' is not satisfied on the current host."));
        }

        return BuildResult(findings);
    }

    // ═══════════════════════════════════════════════════════════════
    // Runtime check (full DB access)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Run all runtime checks. Called at instance-creation time after
    /// parameter values have been bound.
    /// </summary>
    public async Task<TaskPreflightResult> CheckRuntimeAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> paramValues,
        Guid? callerAgentId,
        CancellationToken ct = default)
    {
        var findings = new List<TaskPreflightFinding>();

        foreach (var req in requirements)
        {
            switch (req.Kind)
            {
                case TaskRequirementKind.RequiresPlatform:
                {
                    var passed = Enum.TryParse<TaskPlatform>(req.Value, out var platform)
                                 && IsPlatformSatisfied(platform);
                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed,
                        passed
                            ? $"Platform '{req.Value}' is satisfied."
                            : $"Platform '{req.Value}' is not satisfied on the current host."));
                    break;
                }

                case TaskRequirementKind.RequiresProvider:
                {
                    var value = req.Value ?? string.Empty;
                    bool passed;
                    string message;

                    var knownKeys = WellKnownProviderKeys.All;
                    if (knownKeys.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        var normalised = knownKeys.First(k =>
                            k.Equals(value, StringComparison.OrdinalIgnoreCase));
                        passed = await db.Providers
                            .AnyAsync(p => p.ProviderKey == normalised
                                          && p.EncryptedApiKey != null, ct);
                        message = passed
                            ? $"Provider '{value}' is configured."
                            : $"Provider '{value}' is not configured or has no API key.";
                    }
                    else
                    {
                        passed = false;
                        message = $"'{value}' is not a recognised provider key.";
                    }

                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed, message));
                    break;
                }

                case TaskRequirementKind.RequiresModelCapability:
                {
                    var capName = req.CapabilityValue ?? string.Empty;
                    var tag = capName.ToLowerInvariant();
                    var passed = await db.Models
                        .AnyAsync(m => m.CapabilityTagsRaw != null && m.CapabilityTagsRaw.Contains(tag), ct);
                    var message = passed
                        ? $"A model with capability tag '{capName}' exists."
                        : $"No model with capability tag '{capName}' is registered.";

                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed, message));
                    break;
                }

                case TaskRequirementKind.RequiresModel:
                {
                    var value = req.Value ?? string.Empty;
                    var passed = await db.Models
                        .AnyAsync(m => m.Name == value || m.CustomId == value, ct);
                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed,
                        passed
                            ? $"Model '{value}' is available."
                            : $"Model '{value}' is not registered."));
                    break;
                }

                case TaskRequirementKind.RequiresModule:
                {
                    var moduleId = req.Value ?? string.Empty;
                    var passed = await IsModuleEnabledAsync(moduleId, ct);
                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed,
                        passed
                            ? $"Module '{moduleId}' is enabled."
                            : $"Module '{moduleId}' is not enabled."));
                    break;
                }

                case TaskRequirementKind.RecommendsModule:
                {
                    var moduleId = req.Value ?? string.Empty;
                    var enabled = await IsModuleEnabledAsync(moduleId, ct);
                    // RecommendsModule is always Warning severity — finding always passes structurally;
                    // non-enabled state surfaces as a warning, not a block.
                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), TaskDiagnosticSeverity.Warning, Passed: enabled,
                        enabled
                            ? $"Recommended module '{moduleId}' is enabled."
                            : $"Recommended module '{moduleId}' is not enabled. The task may have reduced functionality."));
                    break;
                }

                case TaskRequirementKind.RequiresPermission:
                {
                    // At instance-creation time we check whether the caller agent holds
                    // the required global flag with any non-Unset clearance.
                    var flagKey = req.Value ?? string.Empty;
                    var passed = callerAgentId is not null
                                 && await AgentHasFlagAsync(callerAgentId.Value, flagKey, ct);
                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed,
                        passed
                            ? $"Caller agent has permission '{flagKey}'."
                            : callerAgentId is null
                                ? $"Permission '{flagKey}' required but no caller agent was supplied."
                                : $"Caller agent does not have permission '{flagKey}'."));
                    break;
                }

                case TaskRequirementKind.ModelIdParameter:
                {
                    var paramName = req.ParameterName ?? string.Empty;
                    if (!paramValues.TryGetValue(paramName, out var rawValue) || rawValue is null)
                    {
                        findings.Add(new TaskPreflightFinding(
                            req.Kind.ToString(), req.Severity, Passed: false,
                            $"Parameter '{paramName}' is required for model ID resolution but was not provided.",
                            paramName));
                        break;
                    }

                    var modelRef = rawValue.ToString() ?? string.Empty;
                    var passed = Guid.TryParse(modelRef, out var modelGuid)
                        ? await db.Models.AnyAsync(m => m.Id == modelGuid, ct)
                        : await db.Models.AnyAsync(m => m.Name == modelRef || m.CustomId == modelRef, ct);

                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed,
                        passed
                            ? $"Model '{modelRef}' (from parameter '{paramName}') is available."
                            : $"Model '{modelRef}' (from parameter '{paramName}') is not registered.",
                        paramName));
                    break;
                }

                case TaskRequirementKind.RequiresCapabilityParameter:
                {
                    var paramName = req.ParameterName ?? string.Empty;
                    var capName = req.CapabilityValue ?? string.Empty;

                    if (!paramValues.TryGetValue(paramName, out var rawValue) || rawValue is null)
                    {
                        findings.Add(new TaskPreflightFinding(
                            req.Kind.ToString(), req.Severity, Passed: false,
                            $"Parameter '{paramName}' is required for capability check but was not provided.",
                            paramName));
                        break;
                    }

                    var modelRef = rawValue.ToString() ?? string.Empty;
                    var passed = false;
                    string message;

                    var tag = capName.ToLowerInvariant();
                    passed = Guid.TryParse(modelRef, out var modelGuid)
                        ? await db.Models.AnyAsync(m => m.Id == modelGuid && m.CapabilityTagsRaw != null && m.CapabilityTagsRaw.Contains(tag), ct)
                        : await db.Models.AnyAsync(m => (m.Name == modelRef || m.CustomId == modelRef) && m.CapabilityTagsRaw != null && m.CapabilityTagsRaw.Contains(tag), ct);
                    message = passed
                        ? $"Model '{modelRef}' (from parameter '{paramName}') has capability tag '{capName}'."
                        : $"Model '{modelRef}' (from parameter '{paramName}') does not have capability tag '{capName}'.";

                    findings.Add(new TaskPreflightFinding(
                        req.Kind.ToString(), req.Severity, passed, message, paramName));
                    break;
                }
            }
        }

        return BuildResult(findings);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static bool IsPlatformSatisfied(TaskPlatform platform)
        => (platform.HasFlag(TaskPlatform.Windows) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        || (platform.HasFlag(TaskPlatform.Linux)   && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        || (platform.HasFlag(TaskPlatform.MacOS)   && RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    private async Task<bool> IsModuleEnabledAsync(string moduleId, CancellationToken ct)
    {
        // External (hot-loaded) modules are always enabled while loaded.
        if (moduleRegistry.GetModule(moduleId) is not null &&
            moduleRegistry.IsExternal(moduleId))
            return true;

        var state = await db.ModuleStates
            .FirstOrDefaultAsync(s => s.ModuleId == moduleId, ct);
        return state?.Enabled ?? false;
    }

    private async Task<bool> AgentHasFlagAsync(Guid agentId, string flagKey, CancellationToken ct)
    {
        // Resolve agent → role → permission set → global flags
        var agent = await db.Agents
            .AsNoTracking()
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agent?.Role?.PermissionSetId is not { } psId)
            return false;

        return await db.GlobalFlags
            .AnyAsync(f => f.PermissionSetId == psId
                        && f.FlagKey == flagKey
                        && f.Clearance != PermissionClearance.Unset, ct);
    }

    private static TaskPreflightResult BuildResult(List<TaskPreflightFinding> findings)
    {
        var isBlocked = findings.Any(f =>
            f.Severity == TaskDiagnosticSeverity.Error && !f.Passed);
        return new TaskPreflightResult(isBlocked, findings);
    }
}
