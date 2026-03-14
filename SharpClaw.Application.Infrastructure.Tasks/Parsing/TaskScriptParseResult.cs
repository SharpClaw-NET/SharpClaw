using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Application.Infrastructure.Tasks.Parsing;

/// <summary>
/// Result of <see cref="TaskScriptParser.Parse"/>.
/// </summary>
public sealed record TaskScriptParseResult(
    TaskScriptDefinition? Definition,
    IReadOnlyList<TaskDiagnostic> Diagnostics)
{
    public bool Success =>
        Definition is not null &&
        !Diagnostics.Any(d => d.Severity == TaskDiagnosticSeverity.Error);
}
