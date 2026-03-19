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
    public static async Task<IResult> Start(
        Guid taskId,
        StartTaskInstanceRequest request,
        TaskService svc,
        SessionService session)
        => Results.Ok(await svc.CreateInstanceAsync(
            request with { TaskDefinitionId = taskId },
            callerUserId: session.UserId));

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
        HttpContext httpContext)
    {
        var reader = orchestrator.GetOutputReader(instanceId);
        if (reader is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        var ct = httpContext.RequestAborted;
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(new
                {
                    type = evt.Type.ToString(),
                    sequence = evt.Sequence,
                    timestamp = evt.Timestamp,
                    data = evt.Data,
                });
                await httpContext.Response.WriteAsync($"data:{json}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
    }
}
