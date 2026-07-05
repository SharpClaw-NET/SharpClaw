using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Contributes chat/output and entity provisioning operation descriptors owned by
/// the agent orchestration module to the central task operation registry.
/// </summary>
public sealed class AgentOrchestrationOperationDescriptorProvider : ITaskOperationDescriptorProvider
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public IReadOnlyList<TaskOperationDescriptor> Descriptors { get; } = Build();

    private static TaskOperationDescriptor[] Build()
    {
        const string owner = "sharpclaw_agent_orchestration";
        return
        [
            // ── Agent interaction ────────────────────────────────────
            new TaskOperationDescriptor
            {
                MethodName         = "Chat",
                OperationKey       = AgentOrchestrationOperationKeys.Chat,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },
            new TaskOperationDescriptor
            {
                MethodName         = "ChatStream",
                OperationKey       = AgentOrchestrationOperationKeys.ChatStream,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },
            new TaskOperationDescriptor
            {
                MethodName         = "ChatToThread",
                OperationKey       = AgentOrchestrationOperationKeys.ChatToThread,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },

            // ── Output ──────────────────────────────────────────────
            new TaskOperationDescriptor
            {
                MethodName           = "Emit",
                OperationKey         = AgentOrchestrationOperationKeys.Emit,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName                  = "ParseResponse",
                OperationKey                = AgentOrchestrationOperationKeys.ParseResponse,
                OwnerId                     = owner,
                CapturesGenericType         = true,
                RequiresDeclaredGenericType = true,
            },

            // ── Entity lookup / creation ────────────────────────────
            new TaskOperationDescriptor
            {
                MethodName           = "FindModel",
                OperationKey         = AgentOrchestrationOperationKeys.FindModel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "FindProvider",
                OperationKey         = AgentOrchestrationOperationKeys.FindProvider,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "FindAgent",
                OperationKey         = AgentOrchestrationOperationKeys.FindAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "CreateAgent",
                OperationKey         = AgentOrchestrationOperationKeys.CreateAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "CreateThread",
                OperationKey         = AgentOrchestrationOperationKeys.CreateThread,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },

            // ── Roles / permissions / channels ──────────────────────
            new TaskOperationDescriptor
            {
                MethodName           = "CreateRole",
                OperationKey         = AgentOrchestrationOperationKeys.CreateRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "FindRole",
                OperationKey         = AgentOrchestrationOperationKeys.FindRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "SetRolePermissions",
                OperationKey         = AgentOrchestrationOperationKeys.SetRolePermissions,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "AssignRole",
                OperationKey         = AgentOrchestrationOperationKeys.AssignRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "CreateChannel",
                OperationKey         = AgentOrchestrationOperationKeys.CreateChannel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "FindChannel",
                OperationKey         = AgentOrchestrationOperationKeys.FindChannel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskOperationDescriptor
            {
                MethodName           = "AddAllowedAgent",
                OperationKey         = AgentOrchestrationOperationKeys.AddAllowedAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
        ];
    }
}
