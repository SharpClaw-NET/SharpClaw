using System.Diagnostics;
using System.Text;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Reads the <c>general.architecture</c> metadata key from a GGUF file header
/// without loading tensor data. Bounded to the first 64 key-value pairs.
/// </summary>
public static class GgufHeaderReader
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    /// <summary>
    /// Returns the value of the <c>general.architecture</c> key, or <c>null</c>
    /// if the file is not a valid GGUF, the key is absent, or any I/O error occurs.
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
            if (!magic.SequenceEqual(GgufMagic)) return null;

            reader.ReadUInt32();                       // version
            reader.ReadUInt64();                       // tensor count
            var kvCount = reader.ReadUInt64();         // metadata KV count

            for (ulong i = 0; i < Math.Min(kvCount, 64UL); i++)
            {
                var keyLen    = reader.ReadUInt64();
                var key       = Encoding.UTF8.GetString(reader.ReadBytes((int)keyLen));
                var valueType = reader.ReadUInt32();

                if (key == "general.architecture" && valueType == 8 /* GGUF_TYPE_STRING */)
                {
                    var valLen = reader.ReadUInt64();
                    return Encoding.UTF8.GetString(reader.ReadBytes((int)valLen));
                }

                SkipValue(reader, valueType);
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SharpClaw.CLI] GgufHeaderReader: {ex.Message}");
            return null;
        }
    }

    // GGUF value types:
    //   0=u8  1=i8  2=u16  3=i16  4=u32  5=i32  6=f32  7=bool
    //   8=str 9=arr 10=u64 11=i64 12=f64
    private static void SkipValue(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7:          r.ReadByte();    break;
            case 2: case 3:                  r.ReadUInt16();  break;
            case 4: case 5: case 6:          r.ReadUInt32();  break;
            case 8:                          r.ReadBytes((int)r.ReadUInt64()); break;
            case 10: case 11: case 12:       r.ReadUInt64();  break;
            case 9:
                var elemType  = r.ReadUInt32();
                var elemCount = r.ReadUInt64();
                for (ulong e = 0; e < elemCount; e++)
                    SkipValue(r, elemType);
                break;
        }
    }
}
