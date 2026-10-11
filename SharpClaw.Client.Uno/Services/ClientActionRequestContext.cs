using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public request-context data. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record ClientActionRequestContext(
    RequestPrincipal Caller,
    ExtensionFeatureSet Features);
