using System.Text.Json;

namespace SharpClaw.Shared.Instances;


/// <summary>Portable settings surfaces declared by a package, not frontend CLR plugins.</summary>
public sealed record SharpClawModuleSettingsPage(
    string SourceId, string ModuleName, string Id, string Title, string ReadPath, string SavePath);
