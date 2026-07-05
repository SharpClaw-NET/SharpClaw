using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Module-side executor for chat / output / provisioning task operations owned by
/// Agent Orchestration.  All real work is delegated to
/// <see cref="IHostAgentBridge"/>, an application-host service
/// resolved from the running task's <see cref="ITaskOperationExecutionContext.Services"/>
/// scope.  This keeps the module free of any direct dependency on Core / EF
/// types while still owning the operation semantics.
/// </summary>
public sealed class AgentOrchestrationTaskOperationExecutor : ITaskOperationExecutor
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public bool CanExecute(string operationKey) => operationKey switch
    {
        AgentOrchestrationOperationKeys.Emit
            or AgentOrchestrationOperationKeys.Chat
            or AgentOrchestrationOperationKeys.ChatStream
            or AgentOrchestrationOperationKeys.ChatToThread
            or AgentOrchestrationOperationKeys.ParseResponse
            or AgentOrchestrationOperationKeys.FindModel
            or AgentOrchestrationOperationKeys.FindProvider
            or AgentOrchestrationOperationKeys.FindAgent
            or AgentOrchestrationOperationKeys.FindRole
            or AgentOrchestrationOperationKeys.FindChannel
            or AgentOrchestrationOperationKeys.CreateAgent
            or AgentOrchestrationOperationKeys.CreateThread
            or AgentOrchestrationOperationKeys.CreateRole
            or AgentOrchestrationOperationKeys.SetRolePermissions
            or AgentOrchestrationOperationKeys.AssignRole
            or AgentOrchestrationOperationKeys.CreateChannel
            or AgentOrchestrationOperationKeys.AddAllowedAgent => true,
        _ => false,
    };

    public async Task<bool> ExecuteAsync(
        string operationKey,
        ITaskOperationExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        if (operationKey == AgentOrchestrationOperationKeys.Emit)
        {
            await context.WriteOutputAsync(expression);
            return true;
        }

        var bridge = context.Services.GetRequiredService<IHostAgentBridge>();
        var ct = context.CancellationToken;
        var taskName = string.Empty;

        switch (operationKey)
        {
            case AgentOrchestrationOperationKeys.Chat:
            {
                var agentId = ParseGuidArg(arguments, 0);
                var content = await bridge.ChatAsync(
                    context.InstanceId, taskName, expression ?? string.Empty, agentId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = content;
                break;
            }
            case AgentOrchestrationOperationKeys.ChatStream:
            {
                var agentId = ParseGuidArg(arguments, 0);
                var content = await bridge.ChatStreamAsync(
                    context.InstanceId, taskName, expression ?? string.Empty, agentId, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = content;
                break;
            }
            case AgentOrchestrationOperationKeys.ChatToThread:
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
            case AgentOrchestrationOperationKeys.ParseResponse:
            {
                var typeName = arguments is { Count: > 0 } ? arguments[0] : null;
                var parsed = bridge.ParseStructuredResponse(
                    context.InstanceId, expression ?? string.Empty, typeName);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = parsed;
                break;
            }
            case AgentOrchestrationOperationKeys.FindModel:
                StoreFindResult(resultVariable,
                    await bridge.FindModelAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationOperationKeys.FindProvider:
                StoreFindResult(resultVariable,
                    await bridge.FindProviderAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationOperationKeys.FindAgent:
                StoreFindResult(resultVariable,
                    await bridge.FindAgentAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationOperationKeys.FindRole:
                StoreFindResult(resultVariable,
                    await bridge.FindRoleAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationOperationKeys.FindChannel:
                StoreFindResult(resultVariable,
                    await bridge.FindChannelAsync(expression ?? string.Empty, ct), context);
                break;
            case AgentOrchestrationOperationKeys.CreateAgent:
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
            case AgentOrchestrationOperationKeys.CreateThread:
            {
                var channelId = ParseGuidArg(arguments, 0);
                var threadName = arguments is { Count: > 1 } ? arguments[1] : null;
                var id = await bridge.CreateThreadAsync(context.InstanceId, channelId, threadName, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = id.ToString();
                break;
            }
            case AgentOrchestrationOperationKeys.CreateRole:
            {
                var id = await bridge.CreateRoleAsync(expression ?? string.Empty, ct);
                if (resultVariable is not null)
                    context.Variables[resultVariable] = id.ToString();
                await context.AppendLogAsync($"CreateRole '{expression}' → {id}");
                break;
            }
            case AgentOrchestrationOperationKeys.SetRolePermissions:
            {
                if (!Guid.TryParse(expression, out var roleId))
                    throw new InvalidOperationException($"SetRolePermissions: invalid role ID '{expression}'.");
                var flagsJson = arguments is { Count: > 1 } ? arguments[1] : null;
                await bridge.SetRolePermissionsAsync(roleId, flagsJson ?? string.Empty, ct);
                await context.AppendLogAsync($"SetRolePermissions {roleId}");
                break;
            }
            case AgentOrchestrationOperationKeys.AssignRole:
            {
                if (!Guid.TryParse(expression, out var agentId))
                    throw new InvalidOperationException($"AssignRole: invalid agent ID '{expression}'.");
                var roleId = ParseGuidArg(arguments, 1)
                    ?? throw new InvalidOperationException("AssignRole: invalid role ID.");
                await bridge.AssignRoleAsync(agentId, roleId, ct);
                await context.AppendLogAsync($"AssignRole agent={agentId} role={roleId}");
                break;
            }
            case AgentOrchestrationOperationKeys.CreateChannel:
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
            case AgentOrchestrationOperationKeys.AddAllowedAgent:
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

    private static void StoreFindResult(string? resultVariable, Guid? id, ITaskOperationExecutionContext context)
    {
        if (resultVariable is not null)
            context.Variables[resultVariable] = id?.ToString();
    }
}
