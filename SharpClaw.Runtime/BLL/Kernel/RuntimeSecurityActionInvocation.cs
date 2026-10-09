using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Redacted input passed to one Runtime security action.</summary>
public sealed record RuntimeSecurityActionInvocation(
    string Operation,
    string Resource);
