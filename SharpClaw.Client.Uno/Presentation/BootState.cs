using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public boot state. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record BootState(
    string Icon,
    Windows.UI.Color IconColor,
    string Text,
    Windows.UI.Color TextColor,
    bool IsRetryVisible,
    ImmutableArray<DiagnosticLine> DiagnosticLog = default);
