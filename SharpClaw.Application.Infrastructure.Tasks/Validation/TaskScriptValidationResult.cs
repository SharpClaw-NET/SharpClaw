using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Infrastructure.Tasks.Validation;

/// <summary>
/// Result of <see cref="TaskScriptValidator.Validate"/>.
/// </summary>
public sealed record TaskScriptValidationResult(
    bool IsValid,
    IReadOnlyList<TaskDiagnostic> Diagnostics);
