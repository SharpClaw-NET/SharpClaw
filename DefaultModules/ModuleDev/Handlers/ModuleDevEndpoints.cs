using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;

namespace SharpClaw.Modules.ModuleDev.Handlers;

/// <summary>
/// Registers REST endpoints for the Module Development Kit.
/// All routes are under <c>/modules/dev</c>.
/// </summary>
public static class ModuleDevEndpoints
{
    /// <summary>
    /// Maps all MDK endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapModuleDevEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/modules/dev");

        // ── Scaffold ─────────────────────────────────────────────
        group.MapPost("/scaffold", async (
            HttpRequest request,
            Services.ModuleScaffoldService scaffold) =>
        {
            var body = await JsonDocument.ParseAsync(request.Body);
            var root = body.RootElement;

            var spec = new Services.ModuleScaffoldService.ScaffoldSpec(
                ModuleId: root.GetProperty("module_id").GetString()!,
                DisplayName: root.GetProperty("display_name").GetString()!,
                ToolPrefix: root.GetProperty("tool_prefix").GetString()!,
                Description: root.TryGetProperty("description", out var d) ? d.GetString() : null);

            var result = await scaffold.ScaffoldAsync(spec);
            return Results.Ok(new { moduleDir = result.ModuleDir, files = result.Files });
        });

        // ── File listing ─────────────────────────────────────────
        group.MapGet("/{moduleId}/files", (
            string moduleId, string? pattern,
            Services.ModuleWorkspaceService workspace) =>
        {
            var files = workspace.ListFiles(moduleId, pattern);
            return Results.Ok(files);
        });

        // ── File read ────────────────────────────────────────────
        group.MapGet("/{moduleId}/files/{**path}", async (
            string moduleId, string path, int? maxLines,
            Services.ModuleWorkspaceService workspace) =>
        {
            try
            {
                var content = await workspace.ReadFileAsync(moduleId, path, maxLines ?? 500);
                return Results.Ok(new { path, content });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = $"File not found: {path}" });
            }
        });

        // ── File write ───────────────────────────────────────────
        group.MapPut("/{moduleId}/files/{**path}", async (
            string moduleId, string path, HttpRequest request,
            Services.ModuleWorkspaceService workspace) =>
        {
            var body = await JsonDocument.ParseAsync(request.Body);
            var content = body.RootElement.GetProperty("content").GetString()!;
            var (fullPath, bytesWritten) = await workspace.WriteFileAsync(moduleId, path, content);
            return Results.Ok(new { path = fullPath, bytes_written = bytesWritten });
        });

        // ── Build ────────────────────────────────────────────────
        group.MapPost("/{moduleId}/build", async (
            string moduleId, HttpRequest request,
            Services.ModuleBuildService build) =>
        {
            var config = "Debug";
            if (request.ContentLength > 0)
            {
                var body = await JsonDocument.ParseAsync(request.Body);
                if (body.RootElement.TryGetProperty("configuration", out var c))
                    config = c.GetString() ?? "Debug";
            }

            var result = await build.BuildAsync(moduleId, config);
            return Results.Ok(result);
        });

        // ── Load ─────────────────────────────────────────────────
        group.MapPost("/{moduleId}/load", async (
            string moduleId,
            ModuleService moduleSvc, ModuleLoader loader,
            Services.ModuleWorkspaceService workspace) =>
        {
            var moduleDir = workspace.ResolveModuleDir(moduleId);
            var result = await moduleSvc.LoadExternalAsync(moduleDir, loader.RootServices);
            return Results.Ok(result);
        });

        // ── Unload ───────────────────────────────────────────────
        group.MapDelete("/{moduleId}/load", async (
            string moduleId, ModuleService moduleSvc) =>
        {
            await moduleSvc.UnloadExternalAsync(moduleId);
            return Results.Ok(new { moduleId, unloaded = true });
        });

        // ── Reload ───────────────────────────────────────────────
        group.MapPost("/{moduleId}/reload", async (
            string moduleId, ModuleService moduleSvc, ModuleLoader loader) =>
        {
            var result = await moduleSvc.ReloadExternalAsync(moduleId, loader.RootServices);
            return Results.Ok(result);
        });

        // ── Inspect process ──────────────────────────────────────
        group.MapGet("/inspect/{target}", async (
            string target, string? include, string? exportFilter,
            Services.ProcessInspectionService inspection) =>
        {
            var sections = include?.Split(',', StringSplitOptions.TrimEntries) as IReadOnlyList<string>;
            var result = await inspection.InspectAsync(target, sections, exportFilter);
            return Results.Content(result, "application/json");
        });

        // ── COM type library inspection ──────────────────────────
        group.MapGet("/com/{**typelibPath}", async (
            string typelibPath, string? interfaceFilter, bool? includeInherited,
            Services.ComTypeLibInspector inspector) =>
        {
            var result = await inspector.InspectAsync(typelibPath, interfaceFilter, includeInherited ?? false);
            return Results.Content(result, "application/json");
        });

        // ── Dev environment ──────────────────────────────────────
        group.MapGet("/env", async (Services.DevEnvironmentService devEnv) =>
        {
            var info = await devEnv.GetEnvironmentAsync();
            return Results.Content(devEnv.ToJson(info), "application/json");
        });

        return routes;
    }
}
