using System.Globalization;

namespace SharpClaw.Runtime.Host;

/// <summary>Drains owned pipes to EOF with bounded, fully redacted diagnostics.</summary>
internal static class SidecarOutputDrain
{
    internal static Task<string> DrainAsync(TextReader reader) =>
        Task.Run(() => DrainCoreAsync(reader), CancellationToken.None);

    private static async Task<string> DrainCoreAsync(TextReader reader)
    {
        var buffer = new char[4096];
        long characters = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0) break;
            characters = characters > long.MaxValue - read ? long.MaxValue : characters + read;
        }
        // Raw arbitrary sidecar output can split sensitive labels across reads.
        // Retain counts rather than an unsafe tail detached from its label.
        return "[REDACTED: sidecar output omitted; characters drained=" +
            characters.ToString(CultureInfo.InvariantCulture) + "]";
    }
}
