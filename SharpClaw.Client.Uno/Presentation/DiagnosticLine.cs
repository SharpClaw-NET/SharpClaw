using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


/// <summary>A single diagnostic probe result shown on the boot page.</summary>
public sealed record DiagnosticLine(
    string Label,
    string Result,
    bool IsError);
