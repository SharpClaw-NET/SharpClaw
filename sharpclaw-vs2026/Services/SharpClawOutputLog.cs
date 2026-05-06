using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;

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
    private readonly List<string> _pending = new();
    private OutputChannel? _channel;

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

            await channel.WriteLineAsync(FormatLine("SharpClaw extension loaded.")).ConfigureAwait(false);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public Task WriteLineAsync(string text)
    {
        var line = FormatLine(text);
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

    private static string FormatLine(string text)
        => $"[{DateTime.Now:HH:mm:ss}] {text}";
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
}
