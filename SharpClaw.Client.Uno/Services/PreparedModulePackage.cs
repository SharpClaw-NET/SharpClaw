using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FileAttributes = System.IO.FileAttributes;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;


/// <summary>An inspected, non-executed candidate whose owned scratch is retired when the view leaves.</summary>
internal sealed class PreparedModulePackage : IDisposable
{
    internal PreparedModulePackage(string root, IReadOnlyList<InstalledModuleIdentity> modules,
        IReadOnlyList<ModulePayloadFile> files)
    { Root = root; Modules = modules; Files = files; }

    internal string Root { get; }
    public IReadOnlyList<InstalledModuleIdentity> Modules { get; }
    internal IReadOnlyList<ModulePayloadFile> Files { get; }
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
