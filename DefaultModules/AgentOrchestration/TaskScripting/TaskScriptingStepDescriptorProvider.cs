using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Contributes the scripting-language and runtime-control step descriptors
/// owned by the task scripting module to the central task step registry.
/// </summary>
public sealed class TaskScriptingStepDescriptorProvider : ITaskStepDescriptorProvider
{
    public string ModuleId => "sharpclaw_agent_orchestration";

    public IReadOnlyList<TaskStepDescriptor> Descriptors { get; } = Build();

    private static TaskStepDescriptor[] Build()
    {
        var owner = "sharpclaw_agent_orchestration";
        return
        [
            // ── Statement constructs (registered by key only) ────────────
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.DeclareVariable, OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.Assign,          OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.EventHandler,    OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.Conditional,     OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.Loop,            OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.Return,          OwnerId = owner },
            new TaskStepDescriptor { StepKey = TaskScriptingStepKeys.Evaluate,        OwnerId = owner },

            // ── Runtime control ────────────────────────────────────────
            new TaskStepDescriptor
            {
                MethodName           = "Delay",
                StepKey              = TaskScriptingStepKeys.Delay,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "WaitUntilStopped",
                StepKey              = TaskScriptingStepKeys.WaitUntilStopped,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "Log",
                StepKey              = TaskScriptingStepKeys.Log,
                OwnerId              = owner,
                FirstArgIsExpression = true,
            },
        ];
    }
}
