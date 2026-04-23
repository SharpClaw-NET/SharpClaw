using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Tasks;

namespace SharpClaw.Application.API.Handlers;

// ═══════════════════════════════════════════════════════════════════
// Task definitions   /tasks
// ═══════════════════════════════════════════════════════════════════

[RouteGroup("/tasks")]
public static class TaskDefinitionHandlers
{
    [MapPost("/validate")]
    public static IResult Validate(
        ValidateTaskDefinitionRequest request,
        TaskService svc)
        => Results.Ok(svc.ValidateDefinition(request.SourceText));

    [MapPost]
    public static async Task<IResult> Create(
        CreateTaskDefinitionRequest request, TaskService svc)
        => Results.Ok(await svc.CreateDefinitionAsync(request));

    [MapGet]
    public static async Task<IResult> List(TaskService svc)
        => Results.Ok(await svc.ListDefinitionsAsync());

    [MapGet("/{taskId:guid}")]
    public static async Task<IResult> GetById(Guid taskId, TaskService svc)
    {
        var def = await svc.GetDefinitionAsync(taskId);
        return def is not null ? Results.Ok(def) : Results.NotFound();
    }

    [MapPut("/{taskId:guid}")]
    public static async Task<IResult> Update(
        Guid taskId, UpdateTaskDefinitionRequest request, TaskService svc)
    {
        var def = await svc.UpdateDefinitionAsync(taskId, request);
        return def is not null ? Results.Ok(def) : Results.NotFound();
    }

    [MapDelete("/{taskId:guid}")]
    public static async Task<IResult> Delete(Guid taskId, TaskService svc)
        => await svc.DeleteDefinitionAsync(taskId) ? Results.NoContent() : Results.NotFound();
}

// ═══════════════════════════════════════════════════════════════════
// Task instances   /tasks/{taskId}/instances
// ═══════════════════════════════════════════════════════════════════

[RouteGroup("/tasks/{taskId:guid}/instances")]
public static class TaskInstanceHandlers
{
    [MapPost]
    public static async Task<IResult> CreateInstance(
        Guid taskId,
        StartTaskInstanceRequest request,
        TaskService svc,
        SessionService session,
        TaskOrchestrator orchestrator)
    {
        var created = await svc.CreateInstanceAsync(
            request with { TaskDefinitionId = taskId },
            callerUserId: session.UserId);

        if ((request with { TaskDefinitionId = taskId }).StartImmediately)
        {
            await orchestrator.StartAsync(created.Id);
            var started = await svc.GetInstanceAsync(created.Id);
            return started is not null ? Results.Ok(started) : Results.Ok(created);
        }

        return Results.Ok(created);
    }

    [MapGet]
    public static async Task<IResult> List(Guid taskId, TaskService svc)
        => Results.Ok(await svc.ListInstancesAsync(taskId));

    [MapGet("/{instanceId:guid}")]
    public static async Task<IResult> GetById(
        Guid taskId, Guid instanceId, TaskService svc, ChatService chatSvc)
    {
        var inst = await svc.GetInstanceAsync(instanceId);
        if (inst is null) return Results.NotFound();
        if (inst.ChannelId is { } chId)
        {
            var cost = await chatSvc.GetChannelCostAsync(chId);
            inst = inst with { ChannelCost = cost };
        }
        return Results.Ok(inst);
    }

    [MapPost("/{instanceId:guid}/cancel")]
    public static async Task<IResult> Cancel(
        Guid taskId, Guid instanceId, TaskService svc)
        => await svc.CancelInstanceAsync(instanceId) ? Results.NoContent() : Results.NotFound();

    [MapPost("/{instanceId:guid}/stop")]
    public static async Task<IResult> Stop(
        Guid taskId, Guid instanceId, TaskOrchestrator orchestrator)
    {
        await orchestrator.StopAsync(instanceId);
        return Results.NoContent();
    }

    [MapPost("/{instanceId:guid}/pause")]
    public static async Task<IResult> Pause(
        Guid taskId,
        Guid instanceId,
        TaskOrchestrator orchestrator)
        => await orchestrator.PauseAsync(instanceId) ? Results.NoContent() : Results.NotFound();

    [MapPost("/{instanceId:guid}/resume")]
    public static async Task<IResult> Resume(
        Guid taskId,
        Guid instanceId,
        TaskOrchestrator orchestrator)
        => await orchestrator.ResumeAsync(instanceId) ? Results.NoContent() : Results.NotFound();

    [MapPost("/{instanceId:guid}/start")]
    public static async Task<IResult> StartExecution(
        Guid taskId, Guid instanceId, TaskOrchestrator orchestrator)
    {
        await orchestrator.StartAsync(instanceId);
        return Results.NoContent();
    }

    [MapGet("/{instanceId:guid}/outputs")]
    public static async Task<IResult> GetOutputs(
        Guid taskId, Guid instanceId, TaskService svc, DateTimeOffset? since = null)
        => Results.Ok(await svc.GetOutputsAsync(instanceId, since));

    [MapGet("/{instanceId:guid}/stream")]
    public static async Task StreamEvents(
        Guid taskId, Guid instanceId,
        TaskOrchestrator orchestrator,
        TaskService svc,
        HttpContext httpContext)
    {
        if (await svc.GetInstanceAsync(instanceId) is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        await TaskStreamHandlers.Stream(httpContext, taskId, instanceId, orchestrator);
    }
}
