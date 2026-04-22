using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace SharpClaw.Utils.Logging;

/// <summary>
/// Writes session-scoped log, debug, and exception text files under the
/// SharpClaw Local AppData folder.
/// </summary>
public sealed class SessionLogWriter : IAsyncDisposable, IDisposable
{
    private readonly Channel<LogWriteEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processorTask;
    private readonly Task _flushTask;
    private readonly StreamWriter _logWriter;
    private readonly StreamWriter _debugWriter;
    private readonly StreamWriter _exceptionWriter;
    private int _disposeState;

    /// <summary>
    /// Creates a session log writer for the provided app name.
    /// Existing session files are deleted on startup.
    /// </summary>
    public SessionLogWriter(string appName, TimeSpan? flushInterval = null)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("App name is required.", nameof(appName));

        AppName = appName;
        DirectoryPath = SharpClawAppDataPaths.GetAppLogDirectory(appName);
        Directory.CreateDirectory(DirectoryPath);

        LogFilePath = Path.Combine(DirectoryPath, "log.txt");
        DebugFilePath = Path.Combine(DirectoryPath, "debug.txt");
        ExceptionFilePath = Path.Combine(DirectoryPath, "exceptions.txt");

        ResetSessionFiles();

        _logWriter = CreateWriter(LogFilePath);
        _debugWriter = CreateWriter(DebugFilePath);
        _exceptionWriter = CreateWriter(ExceptionFilePath);

        _channel = Channel.CreateUnbounded<LogWriteEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _processorTask = Task.Run(ProcessQueueAsync);
        _flushTask = Task.Run(() => FlushLoopAsync(flushInterval ?? TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Gets the logical app name for this writer.
    /// </summary>
    public string AppName { get; }

    /// <summary>
    /// Gets the per-app session log directory.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Gets the regular log file path.
    /// </summary>
    public string LogFilePath { get; }

    /// <summary>
    /// Gets the debug log file path.
    /// </summary>
    public string DebugFilePath { get; }

    /// <summary>
    /// Gets the exception log file path.
    /// </summary>
    public string ExceptionFilePath { get; }

    /// <summary>
    /// Appends a regular log line.
    /// </summary>
    public void AppendLog(string message) =>
        Enqueue(LogStreamKind.Log, message, flushImmediately: false);

    /// <summary>
    /// Appends a debug log line.
    /// </summary>
    public void AppendDebug(string message) =>
        Enqueue(LogStreamKind.Debug, message, flushImmediately: false);

    /// <summary>
    /// Appends exception details and requests an immediate flush.
    /// </summary>
    public void AppendException(string message) =>
        Enqueue(LogStreamKind.Exception, message, flushImmediately: true);

    /// <summary>
    /// Appends exception details and requests an immediate flush.
    /// </summary>
    public void AppendException(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.AppendLine(context);
            builder.AppendLine();
        }

        builder.AppendLine(exception.ToString());
        AppendException(builder.ToString());
    }

    /// <summary>
    /// Writes a debug line both to disk and to the debugger in DEBUG builds.
    /// </summary>
    [Conditional("DEBUG")]
    public void AppendDebugAndTrace(string message, string category)
    {
        Debug.WriteLine(message, category);
        AppendDebug(message);
    }

    /// <summary>
    /// Flushes all open writers.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await FlushWritersAsync().ConfigureAwait(false);
    }

    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _processorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _flushTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await FlushWritersAsync().ConfigureAwait(false);

        await _logWriter.DisposeAsync().ConfigureAwait(false);
        await _debugWriter.DisposeAsync().ConfigureAwait(false);
        await _exceptionWriter.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void ResetSessionFiles()
    {
        DeleteIfExists(LogFilePath);
        DeleteIfExists(DebugFilePath);
        DeleteIfExists(ExceptionFilePath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort reset. If deletion fails, the writer will append.
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
    }

    private void Enqueue(LogStreamKind kind, string message, bool flushImmediately)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);

        var entry = new LogWriteEntry(kind, FormatMessage(message), flushImmediately);
        _channel.Writer.TryWrite(entry);
    }

    private static string FormatMessage(string message)
    {
        var text = message ?? string.Empty;
        return $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {text}";
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            var writer = entry.Kind switch
            {
                LogStreamKind.Log => _logWriter,
                LogStreamKind.Debug => _debugWriter,
                LogStreamKind.Exception => _exceptionWriter,
                _ => _logWriter,
            };

            await writer.WriteLineAsync(entry.Message).ConfigureAwait(false);

            if (entry.FlushImmediately)
                await FlushWritersAsync().ConfigureAwait(false);
        }
    }

    private async Task FlushLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            await FlushWritersAsync().ConfigureAwait(false);
    }

    private async Task FlushWritersAsync()
    {
        await _logWriter.FlushAsync().ConfigureAwait(false);
        await _debugWriter.FlushAsync().ConfigureAwait(false);
        await _exceptionWriter.FlushAsync().ConfigureAwait(false);
    }

    private enum LogStreamKind
    {
        Log,
        Debug,
        Exception,
    }

    private sealed record LogWriteEntry(
        LogStreamKind Kind,
        string Message,
        bool FlushImmediately);
}
