using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.DatabaseAccess.Triggers;

namespace SharpClaw.Modules.DatabaseAccess;

/// <summary>Parser extension exposing module-owned trigger-attribute handlers.</summary>
public sealed class DatabaseAccessParserExtension : ITaskParserModuleExtension
{
    public static readonly DatabaseAccessParserExtension Instance = new();

    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
        DatabaseAccessTriggerAttributeHandlers.All;
}
