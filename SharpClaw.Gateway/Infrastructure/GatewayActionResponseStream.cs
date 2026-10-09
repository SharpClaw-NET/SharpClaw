using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;


internal sealed class GatewayActionResponseStream(
    Stream inner,
    GatewayBackgroundActionBoundary actions,
    GatewayActionInvocation baseInvocation,
    ILogger logger,
    KernelActionExecutionContext executionContext) : Stream
{
    private int _chunk;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var invocation = baseInvocation with
        {
            Operation = "stream.chunk",
            ByteCount = buffer.Length,
        };
        await actions.RunActionAsync(
            new SharpClawActionKey("gateway.stream.chunk.receive"),
            invocation,
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext).ConfigureAwait(false);
        await actions.RunActionAsync(
            new SharpClawActionKey("gateway.stream.chunk.forward"),
            invocation with { Operation = $"stream.chunk.forward:{Interlocked.Increment(ref _chunk)}" },
            async (_, ct) =>
            {
                await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            executionContext).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            logger.LogTrace("Gateway stream response wrapper disposed after {ChunkCount} chunks.", _chunk);
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
