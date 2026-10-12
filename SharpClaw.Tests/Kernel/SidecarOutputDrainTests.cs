using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class SidecarOutputDrainTests
{
    [Test]
    public async Task LongOutputIsFullyDrainedWithoutRetainingRawProcessLifetimeTextAsync()
    {
        using var reader = new EmittingReader(10000);
        var result = await SidecarOutputDrain.DrainAsync(reader).ConfigureAwait(false);
        reader.Remaining.Should().Be(0);
        result.Length.Should().BeLessThan(128);
        result.Should().Contain("REDACTED").And.NotContain("confidential");
        result.Should().Contain("40960000");
    }

    private sealed class EmittingReader(int blocks) : TextReader
    {
        internal int Remaining { get; private set; } = blocks;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Remaining == 0) return ValueTask.FromResult(0);
            Remaining--;
            buffer.Span.Fill('x');
            "confidential".AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(buffer.Length);
        }
    }
}
