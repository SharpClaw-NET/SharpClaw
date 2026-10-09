using System.Text.Json;

namespace SharpClaw.Shared.Instances;


public sealed record SharpClawModuleSettingsDocument(
    int SchemaVersion, IReadOnlyList<SharpClawModuleSettingsField> Fields,
    IReadOnlyDictionary<string, JsonElement> Values);
