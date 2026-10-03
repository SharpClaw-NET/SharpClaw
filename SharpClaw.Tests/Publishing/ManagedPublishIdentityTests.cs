using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SharpClaw.Build;

namespace SharpClaw.Tests.Publishing;

#pragma warning disable CA1515 // NUnit creates this public fixture through reflection; it is not application API.
public sealed class ManagedPublishIdentityTests
#pragma warning restore CA1515
{
    [Test]
    public void IdenticalManagedBytesHaveTheSameIdentityAndLeaveTheCallerStreamOpen()
    {
        var bytes = File.ReadAllBytes(typeof(ManagedPublishIdentityTests).Assembly.Location);
        using var first = new MemoryStream(bytes);
        using var second = new MemoryStream(bytes);

        ManagedPublishIdentity.Fingerprint(first).Should().Be(ManagedPublishIdentity.Fingerprint(second));
        first.CanRead.Should().BeTrue();
    }

    [Test]
    public void ReplacingTheManagedModuleIdentityCannotBeAcceptedAsReadyToRun()
    {
        var bytes = File.ReadAllBytes(typeof(ManagedPublishIdentityTests).Assembly.Location);
        var changed = (byte[])bytes.Clone();
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var metadata = pe.GetMetadataReader();
            var offset = FileOffset(pe, pe.PEHeaders.CorHeader!.MetadataDirectory.RelativeVirtualAddress);
            changed[offset + metadata.GetHeapMetadataOffset(HeapIndex.Guid)] ^= 1;
        }

        using var original = new MemoryStream(bytes);
        using var replacement = new MemoryStream(changed);
        ManagedPublishIdentity.Fingerprint(original).Should().NotBe(ManagedPublishIdentity.Fingerprint(replacement));
    }

    [Test]
    public void ChangingMethodIlCannotBeHiddenByUnchangedNamesOrMetadata()
    {
        var bytes = File.ReadAllBytes(typeof(ManagedPublishIdentityTests).Assembly.Location);
        var changed = (byte[])bytes.Clone();
        using (var pe = new PEReader(new MemoryStream(bytes)))
        {
            var metadata = pe.GetMetadataReader();
            var method = metadata.MethodDefinitions
                .Select(metadata.GetMethodDefinition)
                .Single(definition => metadata.GetString(definition.Name) == nameof(IdentityFixtureMethod));
            var offset = FileOffset(pe, method.RelativeVirtualAddress);
            var headerSize = (changed[offset] & 3) == 2 ? 1 : (changed[offset + 1] >> 4) * 4;
            // Change a ldc.i4 operand, leaving the instruction and PE valid.
            changed[offset + headerSize + 1] ^= 1;
        }

        using var original = new MemoryStream(bytes);
        using var replacement = new MemoryStream(changed);
        ManagedPublishIdentity.Fingerprint(original).Should().NotBe(ManagedPublishIdentity.Fingerprint(replacement));
    }

    private static int FileOffset(PEReader pe, int rva)
    {
        var section = pe.PEHeaders.SectionHeaders.Single(header =>
            rva >= header.VirtualAddress && rva < header.VirtualAddress + Math.Max(header.VirtualSize, header.SizeOfRawData));
        return section.PointerToRawData + rva - section.VirtualAddress;
    }

    private static int IdentityFixtureMethod() => 1234;
}
