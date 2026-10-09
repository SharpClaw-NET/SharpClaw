using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed class ClientActionConflictException(string message) : InvalidOperationException(message);
