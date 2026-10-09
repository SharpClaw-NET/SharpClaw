using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;


internal sealed record GatewayActionInvocation(
    string Method,
    string Path,
    string Operation,
    bool IsStream = false,
    int ByteCount = 0);
