using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>
/// Contributes the HTTP-request step descriptors owned by the HTTP module
/// to the central task step registry. The four script verbs (HttpGet,
/// HttpPost, HttpPut, HttpDelete) all share a single step key.
/// </summary>
public sealed class HttpStepDescriptorProvider : ITaskStepDescriptorProvider
{
    public string ModuleId => "sharpclaw_http";

    public IReadOnlyList<TaskStepDescriptor> Descriptors { get; } = Build();

    private static TaskStepDescriptor[] Build()
    {
        const string owner = "sharpclaw_http";
        return
        [
            new TaskStepDescriptor
            {
                MethodName           = "HttpGet",
                StepKey              = HttpStepKeys.HttpRequest,
                OwnerId              = owner,
                PrefixArgument       = "GET",
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "HttpPost",
                StepKey              = HttpStepKeys.HttpRequest,
                OwnerId              = owner,
                PrefixArgument       = "POST",
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "HttpPut",
                StepKey              = HttpStepKeys.HttpRequest,
                OwnerId              = owner,
                PrefixArgument       = "PUT",
                FirstArgIsExpression = true,
            },
            new TaskStepDescriptor
            {
                MethodName           = "HttpDelete",
                StepKey              = HttpStepKeys.HttpRequest,
                OwnerId              = owner,
                PrefixArgument       = "DELETE",
                FirstArgIsExpression = true,
            },
        ];
    }
}
