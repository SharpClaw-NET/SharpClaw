using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using SharpClaw.Utils.Logging;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Shared writer for the "SharpClaw" Output window pane.
///
/// Implements <see cref="IExtensionInitializer"/> so the SDK invokes
/// <see cref="InitializeAsync"/> once at extension load and we can create the
/// output channel via the documented
/// <c>VisualStudioExtensibility.Views().Output.CreateOutputChannelAsync</c>
/// API. The display name passed in is what surfaces in the Output window's
/// "Show output from:" dropdown.
/// </summary>
internal sealed class SharpClawOutputLog : IExtensionInitializer
{
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly object _fileGate = new();
    private readonly List<string> _pending = new();
    private readonly List<string> _allLines = new();
    private OutputChannel? _channel;
    private bool _fileInitialized;
    private string? _instanceId;
    private string? _instanceDirectory;
    private string? _diagnosticLogPath;
    private string? _outputLogPath;

    public string InstanceId => EnsureFileLogInitialized().InstanceId;
    public string InstanceDirectory => EnsureFileLogInitialized().InstanceDirectory;
    public string DiagnosticLogPath => EnsureFileLogInitialized().DiagnosticLogPath;
    public string OutputLogPath => EnsureFileLogInitialized().OutputLogPath;

    public async Task InitializeAsync(
        ExtensionCore extension,
        IServiceProvider serviceProvider,
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
        => await EnsureInitializedAsync(extensibility, cancellationToken).ConfigureAwait(false);

    public async Task EnsureInitializedAsync(
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
    {
        if (_channel is not null)
            return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is not null)
                return;

            var channel = await extensibility.Views().Output
                .CreateOutputChannelAsync("SharpClaw", cancellationToken)
                .ConfigureAwait(false);

            _channel = channel;

            List<string> pending;
            lock (_pendingGate)
            {
                pending = new List<string>(_pending);
                _pending.Clear();
            }

            foreach (var line in pending)
                await channel.WriteLineAsync(line).ConfigureAwait(false);

            await WriteLineAsync("SharpClaw extension loaded.").ConfigureAwait(false);
            await WriteLineAsync($"Persistent plugin log: {DiagnosticLogPath}").ConfigureAwait(false);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public Task WriteLineAsync(string text)
    {
        var line = FormatLine(text);
        WritePersistentLine(line);

        var channel = _channel;
        if (channel is not null)
            return channel.WriteLineAsync(line);

        lock (_pendingGate)
        {
            _pending.Add(line);
            if (_pending.Count > 200)
                _pending.RemoveAt(0);
        }

        return Task.CompletedTask;
    }

    public void WriteLineImmediate(string text)
    {
        WritePersistentLine(FormatLine(text));
    }

    private static string FormatLine(string text)
        => $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [tid:{Environment.CurrentManagedThreadId}] {text}";

    private (string InstanceId, string InstanceDirectory, string DiagnosticLogPath, string OutputLogPath) EnsureFileLogInitialized()
    {
        lock (_fileGate)
        {
            if (_fileInitialized)
                return (_instanceId!, _instanceDirectory!, _diagnosticLogPath!, _outputLogPath!);

            var pluginsRoot = Path.Combine(
                SharpClawAppDataPaths.GetSharpClawRootDirectory(),
                "instances",
                "plugins");
            Directory.CreateDirectory(pluginsRoot);

            var idFile = Path.Combine(pluginsRoot, "vs2026-plugin.instanceid");
            var id = ReadOrCreateInstanceId(idFile);
            var instanceDirectory = Path.Combine(pluginsRoot, id);
            Directory.CreateDirectory(instanceDirectory);

            _instanceId = id;
            _instanceDirectory = instanceDirectory;
            _diagnosticLogPath = Path.Combine(instanceDirectory, "vs2026-plugin.log");
            _outputLogPath = Path.Combine(instanceDirectory, "output.log");

            var manifestPath = Path.Combine(instanceDirectory, "instance.json");
            var manifest = "{" + Environment.NewLine
                + $"  \"instanceId\": \"{id}\"," + Environment.NewLine
                + $"  \"startedAt\": \"{DateTimeOffset.Now:O}\"," + Environment.NewLine
                + $"  \"processId\": {Environment.ProcessId}" + Environment.NewLine
                + "}" + Environment.NewLine;
            File.WriteAllText(manifestPath, manifest, Encoding.UTF8);

            _allLines.Clear();
            _allLines.Add(FormatLine("SharpClaw VS2026 plugin diagnostic log started."));
            _allLines.Add(FormatLine($"InstanceId={id}"));
            _allLines.Add(FormatLine($"InstanceDirectory={instanceDirectory}"));
            _allLines.Add(FormatLine($"ProcessId={Environment.ProcessId}"));
            PersistAllLinesNoLock();

            _fileInitialized = true;
            return (_instanceId, _instanceDirectory, _diagnosticLogPath, _outputLogPath);
        }
    }

    private void WritePersistentLine(string line)
    {
        lock (_fileGate)
        {
            EnsureFileLogInitialized();
            _allLines.Add(line);
            PersistAllLinesNoLock();
        }
    }

    private void PersistAllLinesNoLock()
    {
        var text = string.Join(Environment.NewLine, _allLines) + Environment.NewLine;
        File.WriteAllText(_diagnosticLogPath!, text, Encoding.UTF8);
        File.WriteAllText(_outputLogPath!, text, Encoding.UTF8);
    }

    private static string ReadOrCreateInstanceId(string idFile)
    {
        try
        {
            if (File.Exists(idFile))
            {
                var existing = File.ReadAllText(idFile).Trim();
                if (Guid.TryParse(existing, out _))
                    return existing;
            }
        }
        catch
        {
            // Fall through and rewrite the id file.
        }

        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(idFile, id, Encoding.UTF8);
        return id;
    }
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
}
