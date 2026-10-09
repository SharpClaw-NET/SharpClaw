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


public sealed class SharpClawRegistrationLoggerFactory(
    ILoggerFactory hostFactory,
    SharpClawRegistrationLogContext context) : ILoggerFactory
{
    public MsLogger CreateLogger(string categoryName) =>
        new RegistrationLogger(hostFactory.CreateLogger(categoryName), context);

    public void AddProvider(ILoggerProvider provider) =>
        throw new InvalidOperationException(
            "Registration logger factories cannot add providers to the host logging pipeline.");

    public void Dispose()
    {
    }

    private sealed class RegistrationLogger(
        MsLogger inner,
        SharpClawRegistrationLogContext context) : MsLogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            using var ownership = SharpClawLogOwnership.Push(context);
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
