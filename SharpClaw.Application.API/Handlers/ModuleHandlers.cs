using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Infrastructure.Logging;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Application.Services;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Handlers;

[RouteGroup("/modules")]
public static class ModuleHandlers
{
    /// <summary>List all bundled modules with their enabled/disabled state.</summary>
    [MapGet]
    public static async Task<IResult> List(ModuleService svc)
    {
        var modules = await svc.ListAsync();
        return Results.Ok(modules);
    }

    /// <summary>Get enriched detail for a single module.</summary>
    [MapGet("/{moduleId}")]
    public static async Task<IResult> Get(string moduleId, ModuleService svc)
    {
        var detail = await svc.GetDetailAsync(moduleId);
        return detail is not null
            ? Results.Ok(detail)
            : Results.NotFound(new { error = $"Unknown module: {moduleId}" });
    }

    /// <summary>
    /// Query the in-memory log ring buffer for a module.
    /// Supports cursor-based pagination via the <c>since</c> timestamp.
    /// </summary>
    [MapGet("/{moduleId}/logs")]
    public static IResult GetLogs(
        string moduleId, ModuleLogService logService,
        string? since = null, string? level = null, int take = 100)
    {
        take = Math.Clamp(take, 1, 500);

        DateTimeOffset? sinceTs = null;
        if (since is not null && DateTimeOffset.TryParse(since, out var parsed))
            sinceTs = parsed;

        LogLevel? minLevel = null;
        if (level is not null && Enum.TryParse<LogLevel>(level, ignoreCase: true, out var lv))
            minLevel = lv;

        var entries = logService.GetEntries(moduleId, sinceTs, minLevel, take);
        var cursor = entries.Count > 0 ? entries[^1].Timestamp.ToString("O") : since;

        return Results.Ok(new
        {
            moduleId,
            entries = entries.Select(e => new
            {
                timestamp = e.Timestamp,
                level = e.Level.ToString(),
                message = e.Message,
                exceptionType = e.ExceptionType,
                stackTrace = e.StackTrace,
            }),
            cursor,
        });
    }

    /// <summary>Get accumulated error or warning entries for a module.</summary>
    [MapGet("/{moduleId}/diagnostics")]
    public static IResult GetDiagnostics(
        string moduleId, ModuleLogService logService,
        string level = "error", int take = 50)
    {
        take = Math.Clamp(take, 1, 200);

        var minLevel = string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Warning
            : LogLevel.Error;

        // For diagnostics we filter to exact level (errors = Error+Critical, warnings = Warning only).
        var allEntries = logService.GetEntries(moduleId, since: null, minLevel: minLevel, take: 500);
        var filtered = level.Equals("warning", StringComparison.OrdinalIgnoreCase)
            ? allEntries.Where(e => e.Level == LogLevel.Warning).Take(take).ToList()
            : allEntries.Where(e => e.Level >= LogLevel.Error).Take(take).ToList();

        return Results.Ok(new
        {
            moduleId,
            level,
            count = filtered.Count,
            entries = filtered.Select(e => new
            {
                timestamp = e.Timestamp,
                level = e.Level.ToString(),
                message = e.Message,
                exceptionType = e.ExceptionType,
                stackTrace = e.StackTrace,
            }),
        });
    }

    /// <summary>Clear the in-memory log buffer for a module.</summary>
    [MapDelete("/{moduleId}/logs")]
    public static IResult ClearLogs(string moduleId, ModuleLogService logService)
    {
        logService.Clear(moduleId);
        return Results.NoContent();
    }

    /// <summary>Enable a bundled module. Registers it, runs initialization, persists state.</summary>
    [MapPost("/{moduleId}/enable")]
    public static async Task<IResult> Enable(string moduleId, ModuleService svc, ModuleLoader moduleLoader)
    {
        try
        {
            var result = await svc.EnableAsync(moduleId, moduleLoader.RootServices);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Disable a bundled module. Shuts it down, unregisters, persists state.</summary>
    [MapPost("/{moduleId}/disable")]
    public static async Task<IResult> Disable(string moduleId, ModuleService svc)
    {
        try
        {
            var result = await svc.DisableAsync(moduleId);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Scan the external-modules directory and load any new modules.</summary>
    [MapPost("/scan")]
    public static async Task<IResult> Scan(ModuleService svc, ModuleLoader moduleLoader)
    {
        var loaded = await svc.ScanExternalModulesAsync(moduleLoader.RootServices);
        return Results.Ok(loaded);
    }

    /// <summary>Reload an external module from its source directory.</summary>
    [MapPost("/{moduleId}/reload")]
    public static async Task<IResult> Reload(string moduleId, ModuleService svc, ModuleLoader moduleLoader)
    {
        try
        {
            var result = await svc.ReloadExternalAsync(moduleId, moduleLoader.RootServices);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Unload an external module.</summary>
    [MapPost("/{moduleId}/unload")]
    public static async Task<IResult> Unload(string moduleId, ModuleService svc)
    {
        try
        {
            await svc.UnloadExternalAsync(moduleId);
            return Results.Ok(new { moduleId, unloaded = true });
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    // ── Metrics ──────────────────────────────────────────────────

    /// <summary>Get aggregated execution metrics for a single module.</summary>
    [MapGet("/{moduleId}/metrics")]
    public static IResult GetMetrics(
        string moduleId, ModuleMetricsCollector metrics, ModuleRegistry registry)
    {
        var module = registry.GetModule(moduleId);
        if (module is null)
            return Results.NotFound(new { error = $"Unknown module: {moduleId}" });

        var snapshot = metrics.GetModuleMetrics(moduleId, module.DisplayName, module.ToolPrefix);
        return Results.Ok(snapshot);
    }

    /// <summary>Get aggregated execution metrics for all loaded modules.</summary>
    [MapGet("/metrics")]
    public static IResult GetAllMetrics(ModuleMetricsCollector metrics, ModuleRegistry registry)
    {
        var snapshots = registry.GetAllModules()
            .Select(m => metrics.GetModuleMetrics(m.Id, m.DisplayName, m.ToolPrefix))
            .ToList();
        return Results.Ok(snapshots);
    }

    /// <summary>Reset execution metrics for a single module.</summary>
    [MapPost("/{moduleId}/metrics/reset")]
    public static IResult ResetModuleMetrics(
        string moduleId, ModuleMetricsCollector metrics, ModuleRegistry registry)
    {
        var module = registry.GetModule(moduleId);
        if (module is null)
            return Results.NotFound(new { error = $"Unknown module: {moduleId}" });

        metrics.ResetModule(module.ToolPrefix);
        return Results.NoContent();
    }

    /// <summary>Reset execution metrics for all modules.</summary>
    [MapPost("/metrics/reset")]
    public static IResult ResetAllMetrics(ModuleMetricsCollector metrics)
    {
        metrics.Reset();
        return Results.NoContent();
    }

    // ── Health ───────────────────────────────────────────────────

    /// <summary>Get last known health status for a single module.</summary>
    [MapGet("/{moduleId}/health")]
    public static IResult GetHealth(string moduleId, ModuleHealthCheckService healthService)
    {
        var status = healthService.GetStatus(moduleId);
        return status is not null
            ? Results.Ok(new { moduleId, status.IsHealthy, status.Message, status.Details })
            : Results.Ok(new { moduleId, isHealthy = (bool?)null, message = "No health check recorded yet" });
    }

    /// <summary>Get last known health status for all modules.</summary>
    [MapGet("/health")]
    public static IResult GetAllHealth(ModuleHealthCheckService healthService)
    {
        var all = healthService.GetAllStatuses();
        var result = all.Select(kv => new
        {
            moduleId = kv.Key,
            kv.Value.IsHealthy,
            kv.Value.Message,
            kv.Value.Details,
        });
        return Results.Ok(result);
    }

    // ── Config ───────────────────────────────────────────────────

    /// <summary>Get all config entries for a module.</summary>
    [MapGet("/{moduleId}/config")]
    public static async Task<IResult> GetAllConfig(
        string moduleId, SharpClawDbContext db, CancellationToken ct)
    {
        var entries = await db.ModuleConfigEntries
            .Where(e => e.ModuleId == moduleId)
            .OrderBy(e => e.Key)
            .Select(e => new { e.Key, e.Value })
            .ToListAsync(ct);

        return Results.Ok(new { moduleId, entries });
    }

    /// <summary>Get a single config entry for a module.</summary>
    [MapGet("/{moduleId}/config/{key}")]
    public static async Task<IResult> GetConfigKey(
        string moduleId, string key, SharpClawDbContext db, CancellationToken ct)
    {
        var entry = await db.ModuleConfigEntries
            .FirstOrDefaultAsync(e => e.ModuleId == moduleId && e.Key == key, ct);

        return entry is not null
            ? Results.Ok(new { entry.Key, entry.Value })
            : Results.NotFound(new { error = $"Config key '{key}' not found for module '{moduleId}'." });
    }

    /// <summary>Upsert a config entry for a module.</summary>
    [MapPut("/{moduleId}/config/{key}")]
    public static async Task<IResult> PutConfigKey(
        string moduleId, string key, ConfigValueBody body,
        SharpClawDbContext db, CancellationToken ct)
    {
        if (key.Length > 128)
            return Results.BadRequest(new { error = "Key exceeds 128 characters." });
        if (body.Value is not null && body.Value.Length > 4096)
            return Results.BadRequest(new { error = "Value exceeds 4096 characters." });

        var entry = await db.ModuleConfigEntries
            .FirstOrDefaultAsync(e => e.ModuleId == moduleId && e.Key == key, ct);

        if (entry is not null)
        {
            entry.Value = body.Value;
        }
        else
        {
            db.ModuleConfigEntries.Add(new ModuleConfigEntryDB
            {
                ModuleId = moduleId,
                Key = key,
                Value = body.Value,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { key, body.Value });
    }

    /// <summary>Delete a config entry for a module.</summary>
    [MapDelete("/{moduleId}/config/{key}")]
    public static async Task<IResult> DeleteConfigKey(
        string moduleId, string key, SharpClawDbContext db, CancellationToken ct)
    {
        var entry = await db.ModuleConfigEntries
            .FirstOrDefaultAsync(e => e.ModuleId == moduleId && e.Key == key, ct);

        if (entry is null)
            return Results.NotFound(new { error = $"Config key '{key}' not found for module '{moduleId}'." });

        db.ModuleConfigEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Request body for config PUT operations.</summary>
    public sealed record ConfigValueBody(string? Value);
}
