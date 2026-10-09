using System.Text.Json;

namespace SharpClaw.Shared.Instances;


public sealed record SharpClawModuleSettingsField(
    string Key, string Label, string Kind, bool Required = false, IReadOnlyList<string>? Choices = null);
