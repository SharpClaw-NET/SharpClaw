using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>Parser extension exposing module-owned trigger-attribute handlers.</summary>
public sealed class HttpParserExtension : ITaskParserModuleExtension
{
    public static readonly HttpParserExtension Instance = new();

    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> TriggerAttributeHandlers { get; } =
        Merge(HttpTriggerAttributeHandlers.All, NetworkTriggerAttributeHandlers.All);

    private static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> Merge(
        params IReadOnlyDictionary<string, ITaskTriggerAttributeHandler>[] sources)
    {
        var merged = new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal);
        foreach (var src in sources)
            foreach (var kvp in src)
                merged[kvp.Key] = kvp.Value;
        return merged;
    }
}
