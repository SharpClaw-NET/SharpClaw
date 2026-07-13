using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class TaskPreflightEngineTests
{
    private readonly TaskPreflightEngine _engine = new();

    [Test]
    public void CheckRuntime_FailsUnknownProviderAndMissingApiKey()
    {
        var requirements = new[]
        {
            Requirement(TaskRequirementKind.RequiresProvider, value: "openai"),
            Requirement(TaskRequirementKind.RequiresProvider, value: "missing")
        };
        var facts = Facts(
            providers:
            [
                new TaskPreflightProviderState(
                    "openai",
                    RequiresApiKey: true,
                    IsConfigured: true,
                    HasApiKey: false)
            ]);

        var result = _engine.CheckRuntime(
            requirements,
            new Dictionary<string, object?>(),
            facts,
            hasCallerAgent: true);

        result.IsBlocked.Should().BeTrue();
        result.Findings.Select(finding => finding.Message)
            .Should().Equal(
                "Provider 'openai' is not configured or has no API key.",
                "'missing' is not a recognised provider key.");
    }

    [Test]
    public void CheckRuntime_PassesProviderWithoutApiKeyWhenPluginDoesNotRequireOne()
    {
        var result = _engine.CheckRuntime(
            [Requirement(TaskRequirementKind.RequiresProvider, value: "ollama")],
            new Dictionary<string, object?>(),
            Facts(
                providers:
                [
                    new TaskPreflightProviderState(
                        "ollama",
                        RequiresApiKey: false,
                        IsConfigured: true,
                        HasApiKey: false)
                ]),
            hasCallerAgent: true);

        result.IsBlocked.Should().BeFalse();
        result.Findings.Single().Passed.Should().BeTrue();
    }

    [Test]
    public void CheckRuntime_UsesModelFactsForModelAndCapabilityRequirements()
    {
        var modelId = Guid.NewGuid();
        var facts = Facts(
            models:
            [
                new TaskPreflightModelState(
                    modelId,
                    "gpt-5",
                    "primary-chat",
                    new HashSet<string>(
                        ["chat", "vision"],
                        StringComparer.OrdinalIgnoreCase))
            ]);
        var requirements = new[]
        {
            Requirement(TaskRequirementKind.RequiresModel, value: "primary-chat"),
            Requirement(
                TaskRequirementKind.RequiresModelCapability,
                capability: "Vision"),
            Requirement(
                TaskRequirementKind.ModelIdParameter,
                parameter: "Model"),
            Requirement(
                TaskRequirementKind.RequiresCapabilityParameter,
                capability: "chat",
                parameter: "Model")
        };

        var result = _engine.CheckRuntime(
            requirements,
            new Dictionary<string, object?> { ["Model"] = modelId.ToString("D") },
            facts,
            hasCallerAgent: true);

        result.IsBlocked.Should().BeFalse();
        result.Findings.Should().OnlyContain(finding => finding.Passed);
    }

    [Test]
    public void CheckRuntime_MissingParameterBlocksWithParameterName()
    {
        var result = _engine.CheckRuntime(
            [
                Requirement(
                    TaskRequirementKind.RequiresCapabilityParameter,
                    capability: "vision",
                    parameter: "Model")
            ],
            new Dictionary<string, object?>(),
            TaskPreflightRuntimeFacts.Empty,
            hasCallerAgent: true);

        result.IsBlocked.Should().BeTrue();
        result.Findings.Single().ParameterName.Should().Be("Model");
        result.Findings.Single().Message.Should()
            .Be("Parameter 'Model' is required for capability check but was not provided.");
    }

    [Test]
    public void CheckRuntime_EvaluatesModuleWarningsAndPermissionCallerState()
    {
        var requirements = new[]
        {
            Requirement(TaskRequirementKind.RecommendsModule, value: "optional"),
            Requirement(TaskRequirementKind.RequiresModule, value: "required"),
            Requirement(TaskRequirementKind.RequiresPermission, value: "CanRun")
        };
        var facts = Facts(
            enabledModuleIds: new HashSet<string>(
                ["required"],
                StringComparer.Ordinal),
            callerPermissionFlags: new HashSet<string>(
                ["CanRun"],
                StringComparer.Ordinal));

        var result = _engine.CheckRuntime(
            requirements,
            new Dictionary<string, object?>(),
            facts,
            hasCallerAgent: true);

        result.IsBlocked.Should().BeFalse();
        result.Findings[0].Severity.Should().Be(TaskDiagnosticSeverity.Warning);
        result.Findings[0].Passed.Should().BeFalse();
        result.Findings[1].Passed.Should().BeTrue();
        result.Findings[2].Passed.Should().BeTrue();
    }

    [Test]
    public void CheckRuntime_BlocksPermissionWhenCallerAgentIsMissing()
    {
        var result = _engine.CheckRuntime(
            [Requirement(TaskRequirementKind.RequiresPermission, value: "CanRun")],
            new Dictionary<string, object?>(),
            Facts(
                callerPermissionFlags: new HashSet<string>(
                    ["CanRun"],
                    StringComparer.Ordinal)),
            hasCallerAgent: false);

        result.IsBlocked.Should().BeTrue();
        result.Findings.Single().Message.Should()
            .Be("Permission 'CanRun' required but no caller agent was supplied.");
    }

    private static TaskRequirementDefinition Requirement(
        TaskRequirementKind kind,
        string? value = null,
        string? capability = null,
        string? parameter = null,
        TaskDiagnosticSeverity severity = TaskDiagnosticSeverity.Error)
    {
        return new TaskRequirementDefinition
        {
            Kind = kind,
            Severity = severity,
            Value = value,
            CapabilityValue = capability,
            ParameterName = parameter
        };
    }

    private static TaskPreflightRuntimeFacts Facts(
        IReadOnlyList<TaskPreflightProviderState>? providers = null,
        IReadOnlyList<TaskPreflightModelState>? models = null,
        IReadOnlySet<string>? enabledModuleIds = null,
        IReadOnlySet<string>? callerPermissionFlags = null)
    {
        return new TaskPreflightRuntimeFacts(
            providers ?? [],
            models ?? [],
            enabledModuleIds
            ?? new HashSet<string>(StringComparer.Ordinal),
            callerPermissionFlags
            ?? new HashSet<string>(StringComparer.Ordinal));
    }
}
