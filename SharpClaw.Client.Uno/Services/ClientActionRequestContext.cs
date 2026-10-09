using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed record ClientActionRequestContext(
    RequestPrincipal Caller,
    ExtensionFeatureSet Features);
