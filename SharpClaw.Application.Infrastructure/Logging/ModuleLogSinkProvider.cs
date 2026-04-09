using Microsoft.Extensions.Logging;

namespace SharpClaw.Application.Infrastructure.Logging;

/// <summary>
/// Logger provider that intercepts log entries for <c>SharpClaw.Modules.*</c>
/// categories and routes them to <see cref="ModuleLogService"/> ring buffers.
/// Non-module categories are ignored (no-op logger).
/// </summary>
public sealed class ModuleLogSinkProvider(ModuleLogService logService) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        if (categoryName.StartsWith(ModuleLogService.CategoryPrefix, StringComparison.Ordinal))
        {
            var moduleId = categoryName[ModuleLogService.CategoryPrefix.Length..];
            return new ModuleLogSinkLogger(logService, moduleId);
        }

        return NullLogger.Instance;
    }

    public void Dispose() { }

    private sealed class ModuleLogSinkLogger(ModuleLogService service, string moduleId) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            service.Append(moduleId, logLevel, formatter(state, exception), exception);
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
