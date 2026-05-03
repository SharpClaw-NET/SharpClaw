namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Implemented by modules that contribute task step descriptors. The host
/// collects all <see cref="ITaskStepDescriptorProvider"/> implementations
/// at startup and registers their descriptors with the task step registry
/// before any task script is parsed.
/// </summary>
public interface ITaskStepDescriptorProvider
{
    /// <summary>The module ID contributing these descriptors.</summary>
    string ModuleId { get; }

    /// <summary>
    /// All descriptors contributed by this module. Each descriptor's
    /// <see cref="TaskStepDescriptor.OwnerId"/> must equal <see cref="ModuleId"/>.
    /// </summary>
    IReadOnlyList<TaskStepDescriptor> Descriptors { get; }
}
