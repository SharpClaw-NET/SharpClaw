using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


/// <summary>A single diagnostic probe result shown on the boot page.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public diagnostic presentation data. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record DiagnosticLine(
    string Label,
    string Result,
    bool IsError);
