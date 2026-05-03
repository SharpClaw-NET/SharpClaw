using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.FilesystemTriggers;

/// <summary>Parser extension exposing module-owned trigger-attribute handlers.</summary>
public sealed class FilesystemTriggersParserExtension : ITaskParserModuleExtension
{
    public static readonly FilesystemTriggersParserExtension Instance = new();

    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
        FilesystemTriggerAttributeHandlers.All;
}
