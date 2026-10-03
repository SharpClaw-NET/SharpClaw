#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SharpClaw.Build;

// ReadyToRun may relocate IL/metadata and add native code, but cannot substitute
// a different managed image. Compare metadata with only relocated RVA columns
// normalized, all method IL/exception state, initialized static-field data,
// and managed resources. Never infer initializer length from adjacent RVAs.
#pragma warning disable CA1515 // PowerShell Add-Type consumers invoke this public entry point from another assembly.
public static class ManagedPublishIdentity
#pragma warning restore CA1515
{
    public static string Fingerprint(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        var bytes = pe.GetMetadata().GetContent().ToArray();
        foreach (var table in new[] { TableIndex.MethodDef, TableIndex.FieldRva })
        {
            var offset = metadata.GetTableMetadataOffset(table);
            var rowSize = metadata.GetTableRowSize(table);
            for (var row = 0; row < metadata.GetTableRowCount(table); row++)
            {
                Array.Clear(bytes, offset + (row * rowSize), sizeof(int));
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            AppendInt(hash, MetadataTokens.GetToken(handle));
            AppendInt(hash, body.MaxStack);
            AppendInt(hash, body.LocalVariablesInitialized ? 1 : 0);
            AppendInt(hash, body.LocalSignature.IsNil ? 0 : MetadataTokens.GetToken(body.LocalSignature));
            hash.AppendData(body.GetILContent().AsSpan());
            foreach (var region in body.ExceptionRegions)
            {
                AppendInt(hash, (int)region.Kind);
                AppendInt(hash, region.TryOffset);
                AppendInt(hash, region.TryLength);
                AppendInt(hash, region.HandlerOffset);
                AppendInt(hash, region.HandlerLength);
                AppendInt(hash, region.Kind == ExceptionRegionKind.Filter ? region.FilterOffset : -1);
                AppendInt(hash, region.Kind == ExceptionRegionKind.Catch ? MetadataTokens.GetToken(region.CatchType) : 0);
            }
        }

        AppendFieldInitializers(hash, pe, metadata);

        var resources = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
        if (resources.Size != 0)
        {
            hash.AppendData(pe.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).AsSpan());
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendFieldInitializers(IncrementalHash hash, PEReader pe, MetadataReader metadata)
    {
        var table = pe.GetMetadata().GetContent().AsSpan();
        var offset = metadata.GetTableMetadataOffset(TableIndex.FieldRva);
        var rowSize = metadata.GetTableRowSize(TableIndex.FieldRva);
        var fields = new HashSet<int>();
        for (var row = 0; row < metadata.GetTableRowCount(TableIndex.FieldRva); row++)
        {
            var data = table.Slice(offset + (row * rowSize), rowSize);
            var rva = BinaryPrimitives.ReadInt32LittleEndian(data);
            var fieldRow = rowSize == 6
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[sizeof(int)..])
                : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(int)..]));
            if (rva <= 0 || fieldRow <= 0 || fieldRow > metadata.GetTableRowCount(TableIndex.Field) || !fields.Add(fieldRow))
            {
                throw new BadImageFormatException("Invalid or duplicate initialized field RVA.");
            }

            var handle = MetadataTokens.FieldDefinitionHandle(fieldRow);
            var field = metadata.GetFieldDefinition(handle);
            if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.HasFieldRVA)) !=
                (FieldAttributes.Static | FieldAttributes.HasFieldRVA) || (field.Attributes & FieldAttributes.Literal) != 0)
            {
                throw new BadImageFormatException("RVA initializer requires a static RVA-backed field.");
            }

            var size = GetInitializerSize(metadata, field);
            var sections = pe.PEHeaders.SectionHeaders.Where(section =>
                rva >= section.VirtualAddress &&
                (long)rva + size <= (long)section.VirtualAddress + section.SizeOfRawData &&
                (long)rva + size <= (long)section.VirtualAddress + section.VirtualSize).ToArray();
            var metadataArea = pe.PEHeaders.CorHeader!.MetadataDirectory;
            if (sections.Length != 1 ||
                (rva < (long)metadataArea.RelativeVirtualAddress + metadataArea.Size &&
                 (long)rva + size > metadataArea.RelativeVirtualAddress))
            {
                throw new BadImageFormatException("Initialized field data is not fully inside one file-backed data area.");
            }

            AppendInt(hash, MetadataTokens.GetToken(handle));
            AppendInt(hash, size);
            hash.AppendData(pe.GetSectionData(rva).GetContent(0, size).AsSpan());
        }

        foreach (var handle in metadata.FieldDefinitions)
        {
            if ((metadata.GetFieldDefinition(handle).Attributes & FieldAttributes.HasFieldRVA) != 0 &&
                !fields.Contains(MetadataTokens.GetRowNumber(handle)))
            {
                throw new BadImageFormatException("RVA-backed field has no initializer row.");
            }
        }
    }

    private static int GetInitializerSize(MetadataReader metadata, FieldDefinition field)
    {
        var signature = metadata.GetBlobReader(field.Signature);
        if (signature.ReadByte() != 0x06)
        {
            throw new BadImageFormatException("Initialized field has an invalid field signature.");
        }

        var code = signature.ReadByte();
        var size = code switch
        {
            0x02 or 0x04 or 0x05 => 1, // Boolean, I1, U1
            0x03 or 0x06 or 0x07 => 2, // Char, I2, U2
            0x08 or 0x09 or 0x0c => 4, // I4, U4, R4
            0x0a or 0x0b or 0x0d => 8, // I8, U8, R8
            0x11 => GetDeclaredValueTypeSize(metadata, signature.ReadTypeHandle()),
            _ => throw new BadImageFormatException("Unsupported RVA field layout; use a verified untransformed image.")
        };
        if (signature.RemainingBytes != 0)
        {
            throw new BadImageFormatException("Unsupported trailing initialized-field signature data.");
        }

        return size;
    }

    private static int GetDeclaredValueTypeSize(MetadataReader metadata, EntityHandle handle)
    {
        // Only fieldless, locally declared, fixed-size value types have a size
        // we can prove without loading code or resolving platform-dependent
        // alignment, external/generic types, pointers, or GC references.
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException("Initialized field uses an unresolved value-type layout.");
        }

        var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
        var layout = type.GetLayout();
        if (type.BaseType.Kind != HandleKind.TypeReference ||
            (type.Attributes & TypeAttributes.LayoutMask) is not (TypeAttributes.SequentialLayout or TypeAttributes.ExplicitLayout) ||
            (type.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Class ||
            type.GetGenericParameters().Count != 0 || layout.Size <= 0 || layout.Size >= 0x100000 ||
            layout.PackingSize is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128) ||
            type.GetFields().Any(field => (metadata.GetFieldDefinition(field).Attributes & FieldAttributes.Static) == 0))
        {
            throw new BadImageFormatException("Initialized value-type size/layout cannot be safely established.");
        }

        var baseType = metadata.GetTypeReference((TypeReferenceHandle)type.BaseType);
        if (!metadata.StringComparer.Equals(baseType.Namespace, "System") ||
            !metadata.StringComparer.Equals(baseType.Name, "ValueType") ||
            baseType.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            throw new BadImageFormatException("Initialized field is not a declared fixed-size value type.");
        }

        return layout.Size;
    }

    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
