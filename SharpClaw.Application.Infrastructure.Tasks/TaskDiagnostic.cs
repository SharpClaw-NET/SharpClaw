namespace SharpClaw.Application.Infrastructure.Tasks;

/// <summary>Severity of a task-script diagnostic.</summary>
public enum TaskDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single diagnostic message produced during parsing, validation,
/// or compilation of a task script.
/// </summary>
public sealed record TaskDiagnostic(
    TaskDiagnosticSeverity Severity,
    string Code,
    string Message,
    int Line = 0,
    int Column = 0);
