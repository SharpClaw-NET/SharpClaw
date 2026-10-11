namespace SharpClaw.Models;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public existing record data. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public record Entity(string Name);
