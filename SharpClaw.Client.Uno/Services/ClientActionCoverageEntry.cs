using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed partial record ClientActionCoverageEntry(
    string Id,
    string Boundary,
    SharpClawActionKey ActionKey);
