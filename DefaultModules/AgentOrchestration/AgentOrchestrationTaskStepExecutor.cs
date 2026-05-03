using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-side executor for chat / output / provisioning task steps owned by
/// Agent Orchestration.  All real work is delegated to
/// <see cref="IHostAgentBridge"/>, an application-host service
/// resolved from the running task's <see cref="ITaskStepExecutionContext.Services"/>
/// scope.  This keeps the module free of any direct dependency on Core / EF
/// types while still owning the step semantics.
/// </summary>
public sealed class AgentOrchestrationTaskStepExecutor : ITaskStepExecutorExtension
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public bool CanExecute(string moduleStepKey) => moduleStepKey switch
    {
        AgentOrchestrationStepKeys.Emit
            or AgentOrchestrationStepKeys.Chat
            or AgentOrchestrationStepKeys.ChatStream
            or AgentOrchestrationStepKeys.ChatToThread
            or AgentOrchestrationStepKeys.ParseResponse
            or AgentOrchestrationStepKeys.FindModel
            or AgentOrchestrationStepKeys.FindProvider
            or AgentOrchestrationStepKeys.FindAgent
            or AgentOrchestrationStepKeys.FindRole
            or AgentOrchestrationStepKeys.FindChannel
            or AgentOrchestrationStepKeys.CreateAgent
            or AgentOrchestrationStepKeys.CreateThread
            or AgentOrchestrationStepKeys.CreateRole
            or AgentOrchestrationStepKeys.SetRolePermissions
            or AgentOrchestrationStepKeys.AssignRole
            or AgentOrchestrationStepKeys.CreateChannel
            or AgentOrchestrationStepKeys.AddAllowedAgent => true,
        _ => false,
    };

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        if (moduleStepKey == AgentOrchestrationStepKeys.Emit)
        {
            await context.WriteOutputAsync(expression);
            return true;
        }

        var bridge = context.Services.GetRequiredService<IHostAgentBridge>();
        var ct = context.CancellationToken;
        var taskName = string.Empty;

        switch (moduleStepKey)
        {
            case AgentOrchestrationStepKeys.Chat:
            {
                var agentId = ParseGuidArg(arguments, 0);
                var content = await bridge.ChatAsync(
                    context.InstanceId, taskName, expression ?? string.Empty, agentId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = content;
                break;
            }
            case AgentOrchestrationStepKeys.ChatStream:
            {
                var agentId = ParseGuidArg(arguments, 0);
                var content = await bridge.ChatStreamAsync(
                    context.InstanceId, taskName, expression ?? string.Empty, agentId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = content;
                break;
            }
            case AgentOrchestrationStepKeys.ChatToThread:
            {
                if (arguments is null || arguments.Count < 1 || !Guid.TryParse(arguments[0], out var threadId))
                    throw new InvalidOperationException(
                        "ChatToThread requires a thread ID as first argument.");
                var agentId = ParseGuidArg(arguments, 2);
                var content = await bridge.ChatToThreadAsync(
                    context.InstanceId, taskName, threadId, expression ?? string.Empty, agentId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = content;
                break;
            }
            case AgentOrchestrationStepKeys.ParseResponse:
            {
                var typeName = arguments is { Count: > 0 } ? arguments[0] : null;
                var parsed = bridge.ParseStructuredResponse(
                    context.InstanceId, expression ?? string.Empty, typeName);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = parsed;
                break;
            }
            case AgentOrchestrationStepKeys.FindModel:
                StoreFindResult(resultVariable,
                    await bridge.FindModelAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationStepKeys.FindProvider:
                StoreFindResult(resultVariable,
                    await bridge.FindProviderAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationStepKeys.FindAgent:
                StoreFindResult(resultVariable,
                    await bridge.FindAgentAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationStepKeys.FindRole:
                StoreFindResult(resultVariable,
                    await bridge.FindRoleAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationStepKeys.FindChannel:
                StoreFindResult(resultVariable,
                    await bridge.FindChannelAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationStepKeys.CreateAgent:
            {
                var name = arguments is { Count: > 0 } ? arguments[0] : "Task Agent";
                var modelId = ParseGuidArg(arguments, 1) ?? Guid.Empty;
                var systemPrompt = arguments is { Count: > 2 } ? arguments[2] : null;
                var customId = arguments is { Count: > 3 } ? arguments[3] : null;
                var id = await bridge.CreateAgentAsync(
                    context.InstanceId, name, modelId, systemPrompt, customId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = id.ToString();
                break;
            }
            case AgentOrchestrationStepKeys.CreateThread:
            {
                var channelId = ParseGuidArg(arguments, 0);
                var threadName = arguments is { Count: > 1 } ? arguments[1] : null;
                var id = await bridge.CreateThreadAsync(context.InstanceId, channelId, threadName, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = id.ToString();
                break;
            }
            case AgentOrchestrationStepKeys.CreateRole:
            {
                var id = await bridge.CreateRoleAsync(expression ?? string.Empty, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = id.ToString();
                await context.AppendLogAsync($"CreateRole '{expression}' → {id}");
                break;
            }
            case AgentOrchestrationStepKeys.SetRolePermissions:
            {
                if (!Guid.TryParse(expression, out var roleId))
                    throw new InvalidOperationException($"SetRolePermissions: invalid role ID '{expression}'.");
                var flagsJson = arguments is { Count: > 1 } ? arguments[1] : null;
                await bridge.SetRolePermissionsAsync(roleId, flagsJson ?? string.Empty, ct);
                await context.AppendLogAsync($"SetRolePermissions {roleId}");
                break;
            }
            case AgentOrchestrationStepKeys.AssignRole:
            {
                if (!Guid.TryParse(expression, out var agentId))
                    throw new InvalidOperationException($"AssignRole: invalid agent ID '{expression}'.");
                var roleId = ParseGuidArg(arguments, 1)
                    ?? throw new InvalidOperationException("AssignRole: invalid role ID.");
                await bridge.AssignRoleAsync(agentId, roleId, ct);
                await context.AppendLogAsync($"AssignRole agent={agentId} role={roleId}");
                break;
            }
            case AgentOrchestrationStepKeys.CreateChannel:
            {
                var title = expression ?? string.Empty;
                var agentId = ParseGuidArg(arguments, 1)
                    ?? throw new InvalidOperationException("CreateChannel: invalid agent ID.");
                var customId = arguments is { Count: > 2 } ? arguments[2] : null;
                var channelId = await bridge.CreateChannelAsync(
                    context.InstanceId, title, agentId, customId, ct);

                if (context.ChannelId == Guid.Empty)
                    context.SetChannelId(channelId);

                if (resultVariable is not null)
                    context.Variables[resultVariable] = channelId.ToString();
                break;
            }
            case AgentOrchestrationStepKeys.AddAllowedAgent:
            {
                if (!Guid.TryParse(expression, out var agentId))
                    throw new InvalidOperationException($"AddAllowedAgent: invalid agent ID '{expression}'.");
                var channelId = ParseGuidArg(arguments, 1);
                await bridge.AddAllowedAgentAsync(context.InstanceId, agentId, channelId, ct);
                break;
            }
        }

        return true;
    }

    private static Guid? ParseGuidArg(IReadOnlyList<string>? args, int index)
    {
        if (args is null || index >= args.Count) return null;
        return Guid.TryParse(args[index], out var g) ? g : null;
    }

    private static void StoreFindResult(string? resultVariable, Guid? id, ITaskStepExecutionContext context)
    {
        if (resultVariable is not null)
            context.Variables[resultVariable] = id?.ToString();
    }
}
