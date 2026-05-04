using SharpClaw.Contracts.DTOs.Tasks;

namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Minimal task definition authoring surface.
/// Lets modules (and any other consumer outside the host application
/// assembly) detect, list, retrieve, validate, create, update, and delete
/// task definitions without taking a project reference on
/// <c>SharpClaw.Application.Core</c>.
///
/// Backed by <c>TaskService</c>; preserves all parse-time validation and
/// trigger synchronisation invariants of the underlying implementation.
/// </summary>
public interface ITaskAuthoring
{
    /// <summary>Parse and validate a task definition without persisting it.</summary>
    TaskValidationResponse ValidateDefinition(string sourceText);

    /// <summary>Create a new task definition from raw C# source.</summary>
    Task<TaskDefinitionResponse> CreateDefinitionAsync(
        CreateTaskDefinitionRequest request,
        CancellationToken ct = default);

    /// <summary>Get a task definition by id, or <see langword="null"/> if missing.</summary>
    Task<TaskDefinitionResponse?> GetDefinitionAsync(Guid id, CancellationToken ct = default);

    /// <summary>Detect (list) every persisted task definition.</summary>
    Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(CancellationToken ct = default);

    /// <summary>Update an existing task definition's source and/or active flag.</summary>
    Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        CancellationToken ct = default);

    /// <summary>Delete a task definition. Returns <see langword="false"/> if not found.</summary>
    Task<bool> DeleteDefinitionAsync(Guid id, CancellationToken ct = default);
}
