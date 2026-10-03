#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SharpClaw.Build;

// ReadyToRun may relocate IL/metadata and add native code, but cannot substitute
// a different managed image. Compare metadata with only relocated RVA columns
// normalized, all method IL/exception state, and managed resources.
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

        var resources = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
        if (resources.Size != 0)
        {
            hash.AppendData(pe.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).AsSpan());
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
