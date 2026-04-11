using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Reads the PE export table from a DLL file using memory-mapped I/O.
/// Pure managed code — no unmanaged dependencies.
/// </summary>
internal static class PeExportReader
{
    /// <summary>
    /// Read exported function names from a PE file.
    /// </summary>
    public static IReadOnlyList<string> ReadExports(string dllPath, string? nameFilter = null)
    {
        if (!File.Exists(dllPath))
            return [];

        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(dllPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            return ReadExportsFromAccessor(accessor, nameFilter);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadExportsFromAccessor(
        MemoryMappedViewAccessor accessor, string? nameFilter)
    {
        // DOS header: e_lfanew at offset 0x3C
        var dosSignature = accessor.ReadUInt16(0);
        if (dosSignature != 0x5A4D) // "MZ"
            return [];

        var peOffset = accessor.ReadInt32(0x3C);
        var peSignature = accessor.ReadUInt32(peOffset);
        if (peSignature != 0x00004550) // "PE\0\0"
            return [];

        // COFF header follows PE signature
        var coffOffset = peOffset + 4;
        var machine = accessor.ReadUInt16(coffOffset);
        var numberOfSections = accessor.ReadUInt16(coffOffset + 2);
        var sizeOfOptionalHeader = accessor.ReadUInt16(coffOffset + 16);

        var optionalHeaderOffset = coffOffset + 20;
        var magic = accessor.ReadUInt16(optionalHeaderOffset);

        // PE32 (0x10B) or PE32+ (0x20B)
        int exportDirRva;
        int exportDirSize;

        if (magic == 0x20B) // PE32+
        {
            exportDirRva = accessor.ReadInt32(optionalHeaderOffset + 112);
            exportDirSize = accessor.ReadInt32(optionalHeaderOffset + 116);
        }
        else if (magic == 0x10B) // PE32
        {
            exportDirRva = accessor.ReadInt32(optionalHeaderOffset + 96);
            exportDirSize = accessor.ReadInt32(optionalHeaderOffset + 100);
        }
        else
        {
            return [];
        }

        if (exportDirRva == 0 || exportDirSize == 0)
            return [];

        // Parse section headers to map RVA → file offset
        var sectionHeaderOffset = optionalHeaderOffset + sizeOfOptionalHeader;
        var sections = new List<(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData)>();

        for (var i = 0; i < numberOfSections; i++)
        {
            var offset = sectionHeaderOffset + (i * 40);
            var virtualSize = accessor.ReadUInt32(offset + 8);
            var virtualAddress = accessor.ReadUInt32(offset + 12);
            var sizeOfRawData = accessor.ReadUInt32(offset + 16);
            var pointerToRawData = accessor.ReadUInt32(offset + 20);
            sections.Add((virtualAddress, virtualSize, pointerToRawData, sizeOfRawData));
        }

        long RvaToOffset(uint rva)
        {
            foreach (var (va, vs, prd, srd) in sections)
            {
                if (rva >= va && rva < va + Math.Max(vs, srd))
                    return rva - va + prd;
            }
            return -1;
        }

        var exportDirOffset = RvaToOffset((uint)exportDirRva);
        if (exportDirOffset < 0)
            return [];

        // Export directory table
        var numberOfNames = accessor.ReadInt32(exportDirOffset + 24);
        var addressOfNamesRva = accessor.ReadUInt32(exportDirOffset + 32);
        var namesOffset = RvaToOffset(addressOfNamesRva);

        if (namesOffset < 0 || numberOfNames <= 0)
            return [];

        Regex? filterRegex = null;
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            try { filterRegex = new Regex(nameFilter, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { /* Invalid regex — ignore filter */ }
        }

        var exports = new List<string>();

        for (var i = 0; i < numberOfNames && i < 10_000; i++)
        {
            var nameRva = accessor.ReadUInt32(namesOffset + (i * 4));
            var nameOffset = RvaToOffset(nameRva);
            if (nameOffset < 0) continue;

            var name = ReadNullTerminatedString(accessor, nameOffset);
            if (string.IsNullOrEmpty(name)) continue;

            if (filterRegex is not null && !filterRegex.IsMatch(name))
                continue;

            exports.Add(name);
        }

        return exports;
    }

    private static string ReadNullTerminatedString(MemoryMappedViewAccessor accessor, long offset)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < 1024; i++)
        {
            var b = accessor.ReadByte(offset + i);
            if (b == 0) break;
            sb.Append((char)b);
        }

        return sb.ToString();
    }
}
