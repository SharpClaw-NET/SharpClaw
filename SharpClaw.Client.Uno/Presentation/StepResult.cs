using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


/// <summary>Result of a single diagnostic step.</summary>
public sealed record StepResult(bool Ok, DiagnosticLine Line, bool CanRetry = true);
