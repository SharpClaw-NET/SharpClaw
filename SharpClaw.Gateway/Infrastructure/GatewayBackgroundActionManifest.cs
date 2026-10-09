using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;


internal static class GatewayBackgroundActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required =>
        GatewayActionManifest.BackgroundRequired;
}
