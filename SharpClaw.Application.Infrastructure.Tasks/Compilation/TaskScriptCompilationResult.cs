using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Infrastructure.Tasks.Compilation;

/// <summary>
/// Result of <see cref="TaskScriptCompiler.Compile"/>.
/// </summary>
public sealed record TaskScriptCompilationResult(
    CompiledTaskPlan? Plan,
    IReadOnlyList<TaskDiagnostic> Diagnostics)
{
    public bool Success =>
        Plan is not null &&
        !Diagnostics.Any(d => d.Severity == TaskDiagnosticSeverity.Error);
}
