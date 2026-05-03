namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Severity level for diagnostics emitted by an
/// <see cref="ITaskTriggerAttributeHandler"/> while parsing a single
/// trigger attribute.
/// </summary>
/// <remarks>
/// Defined here in <c>SharpClaw.Contracts</c> so module-owned attribute
/// handlers can emit diagnostics without taking a dependency on
/// <c>SharpClaw.Application.Infrastructure.Tasks</c>. The parser maps
/// these values onto its internal <c>TaskDiagnosticSeverity</c> when
/// recording handler-emitted diagnostics.
/// </remarks>
public enum TaskTriggerAttributeDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
