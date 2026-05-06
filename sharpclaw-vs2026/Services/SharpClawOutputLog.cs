using System;
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
    private OutputChannel? _channel;

    public async Task InitializeAsync(
        ExtensionCore extension,
        IServiceProvider serviceProvider,
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
    {
        _channel = await extensibility.Views().Output
            .CreateOutputChannelAsync("SharpClaw", cancellationToken)
            .ConfigureAwait(false);

        await WriteLineAsync("SharpClaw extension loaded.").ConfigureAwait(false);
    }

    public Task WriteLineAsync(string text)
    {
        var channel = _channel;
        if (channel is null)
            return Task.CompletedTask;
        return channel.WriteLineAsync($"[{DateTime.Now:HH:mm:ss}] {text}");
    }
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
}
