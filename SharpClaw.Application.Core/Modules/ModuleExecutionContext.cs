namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Scoped sentinel that carries the current module ID during tool execution.
/// Set by the pipeline before calling <c>ExecuteToolAsync</c> or
/// <c>ExecuteInlineToolAsync</c>. Used by <see cref="Contracts.Modules.IModuleConfigStore"/>
/// factory to bind the store to the correct module.
/// </summary>
public sealed class ModuleExecutionContext
{
    /// <summary>
    /// The module ID of the currently executing module.
    /// Null outside of module tool execution.
    /// </summary>
    public string? ModuleId { get; set; }
}
