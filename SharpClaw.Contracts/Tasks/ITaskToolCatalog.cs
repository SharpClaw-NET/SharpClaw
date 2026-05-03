using SharpClaw.Contracts.Providers;

namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Read-only catalog of active task definitions exposed as agent tool
/// schemas. Lets modules surface task tools through their own chat
/// contributors without taking a dependency on <c>TaskToolProvider</c>,
/// <c>SharpClawDbContext</c>, or any infrastructure type.
/// <para>
/// The catalog is policy-free: it always returns every active task
/// definition. The decision of whether to surface those schemas in a
/// given chat turn lives in the calling contributor (typically
/// <c>AgentOrchestrationChatContributor</c>), which evaluates its own
/// module-owned permission flag before invoking the catalog.
/// </para>
/// </summary>
public interface ITaskToolCatalog
{
    /// <summary>
    /// Build tool definitions for all active task definitions.
    /// </summary>
    Task<IReadOnlyList<ChatToolDefinition>> GetToolDefinitionsAsync(
        CancellationToken ct = default);
}
