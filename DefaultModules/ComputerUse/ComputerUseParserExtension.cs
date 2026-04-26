using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.ComputerUse;

/// <summary>
/// Registers the Computer Use module's task-script API methods with
/// <see cref="TaskScriptParser"/>.
/// </summary>
internal sealed class ComputerUseParserExtension : ITaskParserModuleExtension
{
    public static readonly ComputerUseParserExtension Instance = new();

    public IReadOnlyDictionary<string, (string StepKey, string ModuleId)> StepKeyMappings { get; } =
        new Dictionary<string, (string, string)>
        {
            ["ListDisplayDevices"] = ("ListDisplayDevices", "sharpclaw_computer_use"),
        };

    public IReadOnlyDictionary<string, (string TriggerKey, string ModuleId)> EventTriggerMappings { get; } =
        new Dictionary<string, (string, string)>();

    public IReadOnlySet<string> SingleArgExpressionMethods { get; } =
        new HashSet<string>();
}
