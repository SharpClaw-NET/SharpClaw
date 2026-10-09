using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;


internal sealed record GatewayBackgroundTickInvocation(
    string ServiceId,
    string Operation,
    Guid WorkId);
