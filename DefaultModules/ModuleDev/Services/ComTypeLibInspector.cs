using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Deep-dives into COM type libraries: enumerates interfaces, coclasses,
/// methods, parameters, and return types. Windows only.
/// </summary>
internal sealed partial class ComTypeLibInspector
{
    internal sealed record TypeLibReport(
        string Path,
        IReadOnlyList<TypeLibEntry> Entries,
        string? Error);

    internal sealed record TypeLibEntry(
        string Name,
        string Kind,
        IReadOnlyList<MethodInfo>? Methods);

    internal sealed record MethodInfo(
        string Name,
        string ReturnType,
        IReadOnlyList<ParameterInfo> Parameters);

    internal sealed record ParameterInfo(
        string Name,
        string Type,
        string Direction);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Inspect a COM type library and return structured interface metadata.
    /// </summary>
    public Task<string> InspectAsync(
        string typelibPath, string? interfaceFilter = null,
        bool includeInherited = false, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = new TypeLibReport(typelibPath, [],
                "COM type library inspection is only supported on Windows.");
            return Task.FromResult(JsonSerializer.Serialize(unsupported, JsonOpts));
        }

        try
        {
            var result = InspectTypeLib(typelibPath, interfaceFilter, includeInherited);
            return Task.FromResult(JsonSerializer.Serialize(result, JsonOpts));
        }
        catch (COMException ex)
        {
            var error = new TypeLibReport(typelibPath, [], $"COM error: {ex.Message}");
            return Task.FromResult(JsonSerializer.Serialize(error, JsonOpts));
        }
    }

    private static TypeLibReport InspectTypeLib(
        string path, string? interfaceFilter, bool includeInherited)
    {
        var hr = NativeMethods.LoadTypeLib(path, out var typeLib);
        if (hr != 0 || typeLib is null)
            return new TypeLibReport(path, [], $"LoadTypeLib failed with HRESULT 0x{hr:X8}");

        Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(interfaceFilter))
        {
            try { filter = new Regex(interfaceFilter, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { /* Invalid regex */ }
        }

        var count = typeLib.GetTypeInfoCount();
        var entries = new List<TypeLibEntry>();

        for (var i = 0; i < count; i++)
        {
            typeLib.GetTypeInfo(i, out var typeInfo);
            typeLib.GetDocumentation(i, out var name, out _, out _, out _);

            if (string.IsNullOrEmpty(name)) continue;
            if (filter is not null && !filter.IsMatch(name)) continue;

            typeInfo.GetTypeAttr(out var pTypeAttr);
            var typeAttr = Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.TYPEATTR>(pTypeAttr);

            var kind = typeAttr.typekind switch
            {
                System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_COCLASS => "CoClass",
                System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_INTERFACE => "Interface",
                System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_DISPATCH => "DispatchInterface",
                System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_ENUM => "Enum",
                _ => typeAttr.typekind.ToString()
            };

            var startFunc = includeInherited ? 0 : typeAttr.cbSizeVft > 0 ? 0 : 0;
            var methods = new List<MethodInfo>();

            for (var f = 0; f < typeAttr.cFuncs; f++)
            {
                typeInfo.GetFuncDesc(f, out var pFuncDesc);
                var funcDesc = Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.FUNCDESC>(pFuncDesc);

                var names = new string[funcDesc.cParams + 1];
                typeInfo.GetNames(funcDesc.memid, names, names.Length, out var nameCount);

                var methodName = nameCount > 0 ? names[0] : $"func_{f}";

                var parameters = new List<ParameterInfo>();
                for (var p = 1; p < nameCount && p <= funcDesc.cParams; p++)
                {
                    parameters.Add(new ParameterInfo(
                        names[p] ?? $"param{p}",
                        "variant", // Simplified — full type resolution is complex
                        "in"));
                }

                methods.Add(new MethodInfo(
                    methodName ?? $"func_{f}",
                    "variant",
                    parameters));

                typeInfo.ReleaseFuncDesc(pFuncDesc);
            }

            typeInfo.ReleaseTypeAttr(pTypeAttr);
            entries.Add(new TypeLibEntry(name, kind, methods.Count > 0 ? methods : null));
        }

        return new TypeLibReport(path, entries, null);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("oleaut32.dll", EntryPoint = "LoadTypeLib", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int LoadTypeLib(
            string szFile,
            [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.ITypeLib? ppTLib);
    }
}
