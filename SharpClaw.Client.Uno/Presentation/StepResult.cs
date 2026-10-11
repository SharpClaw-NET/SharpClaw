using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


/// <summary>Result of a single diagnostic step.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public boot-step result. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record StepResult(bool Ok, DiagnosticLine Line, bool CanRetry = true);
