using System.Text.Json;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Presentation;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public stream-event result. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record UnoSseEventResult(bool ShouldEnd, bool TextChanged);
