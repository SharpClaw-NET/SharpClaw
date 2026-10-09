using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed record ClientCommandSignal(
    Guid CommandId,
    string Operation);
