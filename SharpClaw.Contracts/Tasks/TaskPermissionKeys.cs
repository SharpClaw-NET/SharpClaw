namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Well-known <see cref="Modules.ModuleGlobalFlagDescriptor.FlagKey"/> values
/// for the built-in task subsystem.
/// </summary>
public static class TaskPermissionKeys
{
    /// <summary>
    /// Grants permission to create, update, and delete task definitions.
    /// An agent without this flag may still execute and view tasks.
    /// </summary>
    public const string CanManageTasks = "CanManageTasks";

    /// <summary>
    /// Grants permission to start, stop, pause, and resume task instances.
    /// </summary>
    public const string CanExecuteTasks = "CanExecuteTasks";

    /// <summary>
    /// Grants permission to invoke task definitions as agent tools.
    /// Agents with this flag see active task definitions in their tool list.
    /// </summary>
    public const string CanInvokeTasksAsTool = "CanInvokeTasksAsTool";
}
