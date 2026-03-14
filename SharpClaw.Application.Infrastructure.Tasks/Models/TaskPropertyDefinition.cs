namespace SharpClaw.Application.Infrastructure.Tasks.Models;

/// <summary>
/// A property on a task-defined data type.
/// </summary>
public sealed record TaskPropertyDefinition(
    string Name,
    string TypeName,
    string? DefaultValue = null,
    bool IsCollection = false,
    /// <summary>Element type when <see cref="IsCollection"/> is <see langword="true"/>.</summary>
    string? ElementTypeName = null);
