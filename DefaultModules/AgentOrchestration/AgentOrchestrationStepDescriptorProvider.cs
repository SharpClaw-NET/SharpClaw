using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Contributes chat/output and entity provisioning step descriptors owned by
/// the agent orchestration module to the central task step registry.
/// </summary>
public sealed class AgentOrchestrationStepDescriptorProvider : ITaskStepDescriptorProvider
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public IReadOnlyList<TaskStepDescriptor> Descriptors { get; } = Build();

    private static TaskStepDescriptor[] Build()
    {
        const string owner = "sharpclaw_agent_orchestration";
        return
        [
            // ── Agent interaction ────────────────────────────────────
            new TaskStepDescriptor
            {
                MethodName         = "Chat",
                StepKey            = AgentOrchestrationStepKeys.Chat,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },
            new TaskStepDescriptor
            {
                MethodName         = "ChatStream",
                StepKey            = AgentOrchestrationStepKeys.ChatStream,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },
            new TaskStepDescriptor
            {
                MethodName         = "ChatToThread",
                StepKey            = AgentOrchestrationStepKeys.ChatToThread,
                OwnerId            = owner,
                ExpressionArgIndex = 1,
            },

            // ── Output ──────────────────────────────────────────────
            new TaskStepDescriptor
            {
                MethodName           = "Emit",
                StepKey              = AgentOrchestrationStepKeys.Emit,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "ParseResponse",
                StepKey              = AgentOrchestrationStepKeys.ParseResponse,
                OwnerId              = owner,
                CapturesGenericType  = true,
            },

            // ── Entity lookup / creation ────────────────────────────
            new TaskStepDescriptor
            {
                MethodName           = "FindModel",
                StepKey              = AgentOrchestrationStepKeys.FindModel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "FindProvider",
                StepKey              = AgentOrchestrationStepKeys.FindProvider,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "FindAgent",
                StepKey              = AgentOrchestrationStepKeys.FindAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "CreateAgent",
                StepKey              = AgentOrchestrationStepKeys.CreateAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "CreateThread",
                StepKey              = AgentOrchestrationStepKeys.CreateThread,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },

            // ── Roles / permissions / channels ──────────────────────
            new TaskStepDescriptor
            {
                MethodName           = "CreateRole",
                StepKey              = AgentOrchestrationStepKeys.CreateRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "FindRole",
                StepKey              = AgentOrchestrationStepKeys.FindRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "SetRolePermissions",
                StepKey              = AgentOrchestrationStepKeys.SetRolePermissions,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "AssignRole",
                StepKey              = AgentOrchestrationStepKeys.AssignRole,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "CreateChannel",
                StepKey              = AgentOrchestrationStepKeys.CreateChannel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "FindChannel",
                StepKey              = AgentOrchestrationStepKeys.FindChannel,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "AddAllowedAgent",
                StepKey              = AgentOrchestrationStepKeys.AddAllowedAgent,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
        ];
    }
}
