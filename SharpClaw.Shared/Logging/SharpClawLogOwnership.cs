using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpClaw.Shared.DurableStorage;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpClaw.Shared.Logging;


public static class SharpClawLogOwnership
{
    private static readonly AsyncLocal<SharpClawRegistrationLogContext?> CurrentContext = new();

    public static SharpClawRegistrationLogContext? Current => CurrentContext.Value;

    public static IDisposable Push(SharpClawRegistrationLogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(SharpClawRegistrationLogContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}
