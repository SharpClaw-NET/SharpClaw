using System.Diagnostics;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace SharpClaw.Shared.Logging;

/// <summary>
/// Installs process-wide bridges that feed console, diagnostics, and Serilog
/// output into the current <see cref="SessionLogWriter"/>.
/// </summary>
public sealed class SessionLogCapture : IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private int _disposed;

    private SessionLogCapture(SessionLogWriter writer)
    {
        _registrations.Add(SessionLogConsoleBridge.Install(writer));
        _registrations.Add(SessionLogDiagnosticsBridge.Install(writer));
    }

    /// <summary>
    /// Installs console and diagnostics capture for the process.
    /// </summary>
    public static SessionLogCapture Install(SessionLogWriter writer) => new(writer);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        for (var i = _registrations.Count - 1; i >= 0; i--)
            _registrations[i].Dispose();
    }
}

/// <summary>
/// Mirrors Serilog events into the session log files.
/// </summary>
public sealed class SessionLogSerilogSink(SessionLogWriter writer) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var rendered = logEvent.RenderMessage();
        var line = $"[Serilog {logEvent.Level}] {rendered}";

        if (logEvent.Level <= LogEventLevel.Debug)
            writer.AppendDebug(line);
        else
            writer.AppendLog(line);

        if (logEvent.Exception is not null)
            writer.AppendException(logEvent.Exception, line);
    }
}

internal sealed class SessionLogConsoleBridge : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    private SessionLogConsoleBridge(SessionLogWriter writer)
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;

        Console.SetOut(new SessionLogTextWriter(_originalOut, writer.AppendLog));
        Console.SetError(new SessionLogTextWriter(_originalError, writer.AppendException));
    }

    public static SessionLogConsoleBridge Install(SessionLogWriter writer) => new(writer);

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }
}

internal sealed class SessionLogDiagnosticsBridge : IDisposable
{
    private readonly SessionLogTraceListener _listener;

    private SessionLogDiagnosticsBridge(SessionLogWriter writer)
    {
        _listener = new SessionLogTraceListener(writer);
        Trace.Listeners.Add(_listener);
    }

    public static SessionLogDiagnosticsBridge Install(SessionLogWriter writer) => new(writer);

    public void Dispose()
    {
        Trace.Listeners.Remove(_listener);
        _listener.Dispose();
    }
}

internal sealed class SessionLogTraceListener(SessionLogWriter writer) : TraceListener
{
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        lock (_gate)
            Append(message);
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            Append(message ?? string.Empty);
            FlushBuffer();
        }
    }

    public override void Fail(string? message, string? detailMessage)
    {
        var text = string.IsNullOrWhiteSpace(detailMessage)
            ? message ?? string.Empty
            : $"{message} {detailMessage}";
        writer.AppendException($"Diagnostics failure: {text}");
    }

    private void Append(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r')
                continue;

            if (ch == '\n')
                FlushBuffer();
            else
                _buffer.Append(ch);
        }
    }

    private void FlushBuffer()
    {
        writer.AppendDebug(_buffer.ToString());
        _buffer.Clear();
    }
}

internal sealed class SessionLogTextWriter(TextWriter inner, Action<string> appendLine) : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        inner.Write(value);
        lock (_gate)
            Append(value);
    }

    public override void Write(string? value)
    {
        inner.Write(value);
        if (value is null)
            return;

        lock (_gate)
        {
            foreach (var ch in value)
                Append(ch);
        }
    }

    public override void WriteLine(string? value)
    {
        inner.WriteLine(value);
        lock (_gate)
        {
            if (value is not null)
            {
                foreach (var ch in value)
                    Append(ch);
            }

            FlushBuffer();
        }
    }

    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Flush();
        base.Dispose(disposing);
    }

    private void Append(char value)
    {
        if (value == '\r')
            return;

        if (value == '\n')
            FlushBuffer();
        else
            _buffer.Append(value);
    }

    private void FlushBuffer()
    {
        appendLine(_buffer.ToString());
        _buffer.Clear();
    }
}
