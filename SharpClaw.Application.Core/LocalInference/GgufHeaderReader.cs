using System.Diagnostics;
using System.IO;
using System.Text;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Reads the <c>general.architecture</c> metadata key from a GGUF file header
/// without loading tensor data. Bounded to the first 64 key-value pairs and
/// guarded against pathological inputs.
/// <para>
/// L-004 hardening: string/byte-array length reads are clamped to
/// <see cref="MaxStringLen"/>, nested array depth is clamped to
/// <see cref="MaxSkipDepth"/>, array element counts are clamped to
/// <see cref="MaxArrayElems"/>, and unknown value types cause an early
/// bail-out instead of silently mis-aligning the stream. Without these
/// bounds, a truncated or hostile GGUF could make the reader allocate
/// multi-GiB buffers, recurse without limit, or read past EOF and throw
/// an unhelpful <see cref="EndOfStreamException"/>.
/// </para>
/// </summary>
public static class GgufHeaderReader
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    // Ceilings picked well above any legitimate GGUF metadata and well
    // below anything that would hurt if we actually allocated it.
    private const int MaxStringLen = 1 * 1024 * 1024;       // 1 MiB
    private const ulong MaxArrayElems = 1_000_000UL;         // 1 M elements
    private const int MaxSkipDepth = 16;
    private const int MaxKvPairs = 64;

    /// <summary>
    /// Returns the value of the <c>general.architecture</c> key, or <c>null</c>
    /// if the file is not a valid GGUF, the key is absent, or any I/O or
    /// validation error occurs.
    /// </summary>
    public static async Task<string?> ReadArchitectureAsync(
        string filePath, CancellationToken ct = default)
    {
        try
        {
            await using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || !magic.SequenceEqual(GgufMagic)) return null;

            reader.ReadUInt32();                       // version
            reader.ReadUInt64();                       // tensor count
            var kvCount = reader.ReadUInt64();         // metadata KV count

            var limit = Math.Min(kvCount, (ulong)MaxKvPairs);
            for (ulong i = 0; i < limit; i++)
            {
                ct.ThrowIfCancellationRequested();

                var key = ReadBoundedString(reader);
                if (key is null) return null;

                var valueType = reader.ReadUInt32();

                if (key == "general.architecture" && valueType == 8 /* GGUF_TYPE_STRING */)
                    return ReadBoundedString(reader);

                if (!TrySkipValue(reader, valueType, depth: 0))
                    return null;
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex)
        {
            Debug.WriteLine($"[SharpClaw.CLI] GgufHeaderReader IO: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[SharpClaw.CLI] GgufHeaderReader access: {ex.Message}");
            return null;
        }
        catch (InvalidDataException ex)
        {
            Debug.WriteLine($"[SharpClaw.CLI] GgufHeaderReader invalid: {ex.Message}");
            return null;
        }
        catch (DecoderFallbackException ex)
        {
            Debug.WriteLine($"[SharpClaw.CLI] GgufHeaderReader utf8: {ex.Message}");
            return null;
        }
    }

    private static string? ReadBoundedString(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > (ulong)MaxStringLen)
            throw new InvalidDataException($"GGUF string length {len} exceeds {MaxStringLen}.");
        var bytes = r.ReadBytes((int)len);
        if (bytes.Length != (int)len)
            throw new InvalidDataException("GGUF string truncated.");
        return Encoding.UTF8.GetString(bytes);
    }

    // GGUF value types:
    //   0=u8  1=i8  2=u16  3=i16  4=u32  5=i32  6=f32  7=bool
    //   8=str 9=arr 10=u64 11=i64 12=f64
    private static bool TrySkipValue(BinaryReader r, uint type, int depth)
    {
        if (depth > MaxSkipDepth)
            throw new InvalidDataException($"GGUF nested array depth exceeded {MaxSkipDepth}.");

        switch (type)
        {
            case 0: case 1: case 7: r.ReadByte(); return true;
            case 2: case 3:         r.ReadUInt16(); return true;
            case 4: case 5: case 6: r.ReadUInt32(); return true;
            case 10: case 11: case 12: r.ReadUInt64(); return true;
            case 8:
            {
                var len = r.ReadUInt64();
                if (len > (ulong)MaxStringLen)
                    throw new InvalidDataException($"GGUF string length {len} exceeds {MaxStringLen}.");
                var consumed = r.Read(new byte[(int)len]);
                if (consumed != (int)len)
                    throw new InvalidDataException("GGUF string truncated.");
                return true;
            }
            case 9:
            {
                var elemType = r.ReadUInt32();
                var elemCount = r.ReadUInt64();
                if (elemCount > MaxArrayElems)
                    throw new InvalidDataException($"GGUF array length {elemCount} exceeds {MaxArrayElems}.");
                for (ulong e = 0; e < elemCount; e++)
                    if (!TrySkipValue(r, elemType, depth + 1))
                        return false;
                return true;
            }
            default:
                // Unknown type — can't realign, bail.
                return false;
        }
    }
}
