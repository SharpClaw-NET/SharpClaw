using System.Reflection;
using System.Text.Json;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Sidecar;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules.Sidecar;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class SidecarReadinessInventoryTests
{
    private static readonly string[] ExpectedModuleDlls =
    [
        "SharpClaw.Modules.AgentOrchestration.dll",
        "SharpClaw.Modules.EditorCommon.dll",
        "SharpClaw.Modules.Metrics.dll",
        "SharpClaw.Modules.ModuleDev.dll",
        "SharpClaw.Modules.Providers.Anthropic.dll",
        "SharpClaw.Modules.Providers.Google.dll",
        "SharpClaw.Modules.Providers.LlamaSharp.dll",
        "SharpClaw.Modules.Providers.Ollama.dll",
        "SharpClaw.Modules.Providers.OpenAICompatible.dll",
        "SharpClaw.Modules.TestHarness.dll",
        "SharpClaw.Modules.VS2026Editor.dll",
        "SharpClaw.Modules.VSCodeEditor.dll",
    ];

    private static readonly Dictionary<string, string[]> ExpectedBlockerKeys = new(StringComparer.Ordinal)
    {
        ["sharpclaw_agent_orchestration"] = [],
        ["sharpclaw_editor_common"] = [],
        ["sharpclaw_metrics"] = [],
        ["sharpclaw_module_dev"] = [],
        ["sharpclaw_providers_anthropic"] = [],
        ["sharpclaw_providers_google"] = [],
        ["sharpclaw_providers_llamasharp"] = [],
        ["sharpclaw_providers_ollama"] = [],
        ["sharpclaw_providers_openai_compat"] = [],
        ["sharpclaw_test_harness"] = [],
        ["sharpclaw_vs2026_editor"] = [],
        ["sharpclaw_vscode_editor"] = [],
    };

    [Test]
    public void BundledSidecarReadinessInventoryIncludesEveryBundledModule()
    {
        var reports = AnalyzeBundledModules();

        reports.Select(report => report.ModuleId)
            .Should()
            .Equal(ExpectedBlockerKeys.Keys.Order(StringComparer.Ordinal));

        reports.Should().OnlyContain(report => !string.IsNullOrWhiteSpace(report.ModuleType));
        reports.Should().OnlyContain(report => !string.IsNullOrWhiteSpace(report.AssemblyName));
    }

    [Test]
    public void BundledSidecarReadinessInventoryCapturesKnownProtocolGaps()
    {
        var reports = AnalyzeBundledModules();
        var expected = ExpectedBlockerKeys.ToDictionary(
            pair => pair.Key,
            pair => string.Join("|", pair.Value.Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var actual = reports.ToDictionary(
            report => report.ModuleId,
            report => string.Join("|", report.Blockers.Select(finding => finding.Key).Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);

        actual.Should().Equal(expected);
        reports.Where(report => report.IsReadyForSidecarDefault)
            .Select(report => report.ModuleId)
            .Should()
            .Equal(
            [
                "sharpclaw_agent_orchestration",
                "sharpclaw_editor_common",
                "sharpclaw_metrics",
                "sharpclaw_module_dev",
                "sharpclaw_providers_anthropic",
                "sharpclaw_providers_google",
                "sharpclaw_providers_llamasharp",
                "sharpclaw_providers_ollama",
                "sharpclaw_providers_openai_compat",
                "sharpclaw_test_harness",
                "sharpclaw_vs2026_editor",
                "sharpclaw_vscode_editor",
            ]);
    }

    [Test]
    public void BundledModulesOptedIntoSidecarHostModeMustBeReadinessClean()
    {
        var reports = AnalyzeBundledModules()
            .ToDictionary(report => report.ModuleId, StringComparer.Ordinal);
        var sidecarModuleIds = LoadBundledManifests()
            .Where(entry => entry.RuntimeInfo.IsSidecarHostMode)
            .Select(entry => entry.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        sidecarModuleIds.Should().Contain("sharpclaw_test_harness");
        foreach (var moduleId in sidecarModuleIds)
        {
            reports.Should().ContainKey(moduleId);
            reports[moduleId].Blockers.Should().BeEmpty(
                $"module '{moduleId}' opted into hostMode=sidecar and must stay protocol-ready");
        }
    }

    [Test]
    public void ReadinessCleanBundledModulesMustDeclareSidecarHostMode()
    {
        var readyModuleIds = AnalyzeBundledModules()
            .Where(report => report.IsReadyForSidecarDefault)
            .Select(report => report.ModuleId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sidecarModuleIds = LoadBundledManifests()
            .Where(entry => entry.RuntimeInfo.IsSidecarHostMode)
            .Select(entry => entry.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        sidecarModuleIds.Should().Equal(
            readyModuleIds,
            "phase two should route every readiness-clean bundled module through the .NET sidecar manifest path");
    }

    [Test]
    public void BundledSidecarReadinessInventoryDistinguishesCoveredProtocolSurfaces()
    {
        var reports = AnalyzeBundledModules().ToDictionary(report => report.ModuleId, StringComparer.Ordinal);

        reports["sharpclaw_agent_orchestration"].Findings
            .Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "tools.job");

        reports["sharpclaw_test_harness"].Findings
            .Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "tools.inline")
            .And
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "tools.job");

        reports["sharpclaw_editor_common"].Findings
            .Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "endpoints.http");
    }

    private static IReadOnlyList<ModuleSidecarReadinessReport> AnalyzeBundledModules()
    {
        var modules = LoadBundledModules();
        var analyzer = new SidecarReadinessAnalyzer();
        return analyzer.AnalyzeAll(modules);
    }

    private static IReadOnlyList<ISharpClawCoreModule> LoadBundledModules()
    {
        var apiOutputDir = ResolveApiOutputDirectory();
        var moduleType = typeof(ISharpClawCoreModule);
        var modules = new List<ISharpClawCoreModule>();

        foreach (var dllName in ExpectedModuleDlls)
        {
            var dllPath = Path.Combine(apiOutputDir, dllName);
            File.Exists(dllPath).Should().BeTrue($"'{dllName}' must be present in API output");

            var assembly = Assembly.LoadFrom(dllPath);
            var implementations = assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                               && moduleType.IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            implementations.Should().ContainSingle(
                $"'{dllName}' must contain exactly one public parameterless ISharpClawCoreModule implementation");

            modules.Add((ISharpClawCoreModule)Activator.CreateInstance(implementations[0])!);
        }

        return modules.OrderBy(module => module.Id, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveApiOutputDirectory()
    {
        var testBinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var solutionRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", ".."));
        var config = new DirectoryInfo(testBinDir).Parent!.Name;
        var tfm = new DirectoryInfo(testBinDir).Name;

        return Path.Combine(solutionRoot, "SharpClaw.Application.API", "bin", config, tfm);
    }

    private static IReadOnlyList<(ModuleManifest Manifest, ModuleManifestRuntimeInfo RuntimeInfo)>
        LoadBundledManifests()
    {
        var modulesDir = Path.Combine(ResolveApiOutputDirectory(), "modules");
        Directory.Exists(modulesDir).Should().BeTrue();

        return Directory.EnumerateFiles(modulesDir, "module.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                var json = File.ReadAllText(path);
                var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)!;
                return (Manifest: manifest, RuntimeInfo: ModuleManifestRuntimeInfo.FromJson(json));
            })
            .OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
