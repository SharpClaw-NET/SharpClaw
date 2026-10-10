using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SharpClaw.Build;

namespace SharpClaw.Tests.Publishing;

#pragma warning disable CA1515 // NUnit creates this public fixture through reflection; it is not application API.
public sealed class ManagedPublishIdentityTests
#pragma warning restore CA1515
{
    private static readonly byte[] InitializerValues = [0x23, 0x41, 0x17, 0x55, 0xa1, 0xe4, 0x33, 0x19, 0x82, 0x01, 0xc9, 0x7a, 0x66, 0x49, 0x02, 0x35];

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
                .Where(definition => string.Equals(metadata.GetString(definition.Name), nameof(IdentityFixtureMethod), StringComparison.Ordinal)).Should().ContainSingle().Which;
            var offset = FileOffset(pe, method.RelativeVirtualAddress);
            var headerSize = (changed[offset] & 3) == 2 ? 1 : (changed[offset + 1] >> 4) * 4;
            // Change a ldc.i4 operand, leaving the instruction and PE valid.
            changed[offset + headerSize + 1] ^= 1;
        }

        using var original = new MemoryStream(bytes);
        using var replacement = new MemoryStream(changed);
        ManagedPublishIdentity.Fingerprint(original).Should().NotBe(ManagedPublishIdentity.Fingerprint(replacement));
    }

    [Test]
    public void ChangingAnRvaInitializerByteCannotBeHiddenByUnchangedMvidMetadataAndIl()
    {
        var bytes = BuildInitializedFieldImage();
        var changed = (byte[])bytes.Clone();
        using var pe = new PEReader(new MemoryStream(bytes));
        var metadata = pe.GetMetadataReader();
        var field = metadata.GetFieldDefinition(metadata.FieldDefinitions.Should().ContainSingle().Which);
        changed[FileOffset(pe, field.GetRelativeVirtualAddress()) + 7] ^= 1;
        using var replacementPe = new PEReader(new MemoryStream(changed));
        var replacementMetadata = replacementPe.GetMetadataReader();

        replacementPe.GetMetadata().GetContent().Should().Equal(pe.GetMetadata().GetContent());
        replacementMetadata.GetGuid(replacementMetadata.GetModuleDefinition().Mvid)
            .Should().Be(metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
        var method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Should().ContainSingle().Which);
        var replacementMethod = replacementMetadata.GetMethodDefinition(replacementMetadata.MethodDefinitions.Should().ContainSingle().Which);
        replacementPe.GetMethodBody(replacementMethod.RelativeVirtualAddress).GetILContent()
            .Should().Equal(pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent());

        Fingerprint(changed).Should().NotBe(Fingerprint(bytes));
    }

    [Test]
    public void RelocatedRvaInitializersWithUnchangedLayoutAndBytesHaveTheSameIdentity()
    {
        var bytes = BuildInitializedFieldImage();
        var relocated = BuildInitializedFieldImage(dataOffset: 32);
        using var first = new PEReader(new MemoryStream(bytes));
        using var second = new PEReader(new MemoryStream(relocated));
        var firstMetadata = first.GetMetadataReader();
        var secondMetadata = second.GetMetadataReader();

        firstMetadata.GetFieldDefinition(firstMetadata.FieldDefinitions.Should().ContainSingle().Which).GetRelativeVirtualAddress()
            .Should().NotBe(secondMetadata.GetFieldDefinition(secondMetadata.FieldDefinitions.Should().ContainSingle().Which).GetRelativeVirtualAddress());
        relocated.Should().NotEqual(bytes);
        Fingerprint(relocated).Should().Be(Fingerprint(bytes));
    }

    [TestCase(0x05, 1)]
    [TestCase(0x06, 2)]
    [TestCase(0x08, 4)]
    [TestCase(0x0a, 8)]
    public void FixedWidthPrimitiveInitializerUsesExactlyItsDeclaredSize(byte typeCode, int size)
    {
        var bytes = BuildInitializedFieldImage(typeCode: typeCode);
        var changed = (byte[])bytes.Clone();
        using var pe = new PEReader(new MemoryStream(bytes));
        var metadata = pe.GetMetadataReader();
        var offset = FileOffset(pe, metadata.GetFieldDefinition(metadata.FieldDefinitions.Should().ContainSingle().Which).GetRelativeVirtualAddress());
        changed[offset + size - 1] ^= 1;

        Fingerprint(changed).Should().NotBe(Fingerprint(bytes));
        changed[offset + size - 1] ^= 1;
        changed[offset + size] ^= 1; // Unreferenced data must not invent a longer field.
        Fingerprint(changed).Should().Be(Fingerprint(bytes));
    }

    [TestCase(0x18)] // Native int has target-dependent width.
    [TestCase(0x1c)] // Object would contain a GC reference.
    public void UnsupportedInitializerSignaturesFailClosed(byte typeCode)
    {
        var bytes = BuildInitializedFieldImage(typeCode: typeCode);

        Action fingerprint = () => Fingerprint(bytes);
        fingerprint.Should().Throw<BadImageFormatException>();
    }

    [TestCase(0)]
    [TestCase(0x100000)]
    public void UnestablishedOrUnboundedDeclaredLayoutFailsClosed(int declaredSize)
    {
        var bytes = BuildInitializedFieldImage(declaredSize: declaredSize);

        Action fingerprint = () => Fingerprint(bytes);
        fingerprint.Should().Throw<BadImageFormatException>();
    }

    [Test]
    public void RvaInitializerOutsideTheFileBackedSectionFailsClosed()
    {
        var bytes = BuildInitializedFieldImage();
        using var pe = new PEReader(new MemoryStream(bytes));
        var metadata = pe.GetMetadataReader();
        var offset = FileOffset(pe, pe.PEHeaders.CorHeader!.MetadataDirectory.RelativeVirtualAddress) +
            metadata.GetTableMetadataOffset(TableIndex.FieldRva);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        Action fingerprint = () => Fingerprint(bytes);
        fingerprint.Should().Throw<BadImageFormatException>();
    }

    private static string Fingerprint(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return ManagedPublishIdentity.Fingerprint(stream);
    }

    private static byte[] BuildInitializedFieldImage(int dataOffset = 0, byte typeCode = 0x11, int declaredSize = 16)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("InitializedFieldFixture.dll"),
            metadata.GetOrAddGuid(new Guid(0x46ba5d48, 0x8998, 0x4a72, 0x94, 0xea, 0x46, 0xb8, 0xbb, 0x70, 0x29, 0x22)), default, default);
        var core = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0),
            default, default, default, default);
        var valueType = metadata.AddTypeReference(core, metadata.GetOrAddString("System"), metadata.GetOrAddString("ValueType"));
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var dataType = metadata.AddTypeDefinition(TypeAttributes.NotPublic | TypeAttributes.ExplicitLayout | TypeAttributes.Sealed,
            default, metadata.GetOrAddString("FixedData"), valueType,
            MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(2));
        metadata.AddTypeLayout(dataType, 1, checked((uint)declaredSize));
        var fieldSignature = new BlobBuilder();
        fieldSignature.WriteByte(0x06);
        fieldSignature.WriteByte(typeCode);
        if (typeCode == 0x11) { fieldSignature.WriteCompressedInteger(MetadataTokens.GetRowNumber(dataType) << 2); }
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            metadata.GetOrAddString("Data"), metadata.GetOrAddBlob(fieldSignature));
        metadata.AddFieldRelativeVirtualAddress(field, dataOffset);
        var mappedData = new BlobBuilder();
        mappedData.WriteBytes(0, dataOffset);
        mappedData.WriteBytes(InitializerValues);
        var il = new BlobBuilder();
        var instructions = new InstructionEncoder(new BlobBuilder());
        instructions.LoadConstantI4(1234);
        instructions.OpCode(ILOpCode.Ret);
        var body = new MethodBodyStreamEncoder(il).AddMethodBody(instructions, maxStack: 1);
        var methodSignature = new BlobBuilder();
        new BlobEncoder(methodSignature).MethodSignature().Parameters(0, result => result.Type().Int32(), _ => { });
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("UnchangedMethod"), metadata.GetOrAddBlob(methodSignature), body, MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), il,
            mappedFieldData: mappedData, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }

    private static int FileOffset(PEReader pe, int rva)
    {
        var section = pe.PEHeaders.SectionHeaders.Where(header =>
            rva >= header.VirtualAddress && rva < header.VirtualAddress + Math.Max(header.VirtualSize, header.SizeOfRawData)).Should().ContainSingle().Which;
        return section.PointerToRawData + rva - section.VirtualAddress;
    }

    private static int IdentityFixtureMethod() => 1234;
}
