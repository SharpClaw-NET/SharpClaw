using System.Reflection;
using System.Text.Json;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Sidecar;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules.Sidecar;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class SidecarReadinessInventoryTests
{
    private static readonly string[] ExpectedPackagedRegistrationDlls =
    [
        "SharpClaw.Modules.AgentOrchestration.dll",
        "SharpClaw.Modules.EditorCommon.dll",
        "SharpClaw.Modules.Metrics.dll",
        "SharpClaw.Modules.RegistrationDev.dll",
        "SharpClaw.Modules.Providers.Anthropic.dll",
        "SharpClaw.Modules.Providers.Google.dll",
        "SharpClaw.Modules.Providers.LlamaSharp.dll",
        "SharpClaw.Modules.Providers.Ollama.dll",
        "SharpClaw.Modules.Providers.OpenAICompatible.dll",
        "SharpClaw.Modules.VS2026Editor.dll",
        "SharpClaw.Modules.VSCodeEditor.dll",
    ];

    private static readonly Dictionary<string, string[]> ExpectedPackagedBlockerKeys = new(StringComparer.Ordinal)
    {
        ["sharpclaw_agent_orchestration"] = [],
        ["sharpclaw_editor_common"] = [],
        ["sharpclaw_metrics"] = [],
        ["sharpclaw_registration_dev"] = [],
        ["sharpclaw_providers_anthropic"] = [],
        ["sharpclaw_providers_google"] = [],
        ["sharpclaw_providers_llamasharp"] = [],
        ["sharpclaw_providers_ollama"] = [],
        ["sharpclaw_providers_openai_compat"] = [],
        ["sharpclaw_vs2026_editor"] = [],
        ["sharpclaw_vscode_editor"] = [],
    };

    [Test]
    public void BundledSidecarReadinessInventoryIncludesEveryBundledRegistration()
    {
        var reports = AnalyzeBundledRegistrations();
        var expectedBlockerKeys = ExpectedBlockerKeys();

        reports.Select(report => report.SourceId)
            .Should()
            .Equal(expectedBlockerKeys.Keys.Order(StringComparer.Ordinal));

        reports.Should().OnlyContain(report => !string.IsNullOrWhiteSpace(report.EntryType));
        reports.Should().OnlyContain(report => !string.IsNullOrWhiteSpace(report.AssemblyName));
    }

    [Test]
    public void BundledSidecarReadinessInventoryCapturesKnownProtocolGaps()
    {
        var reports = AnalyzeBundledRegistrations();
        var expected = ExpectedBlockerKeys().ToDictionary(
            pair => pair.Key,
            pair => string.Join("|", pair.Value.Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var actual = reports.ToDictionary(
            report => report.SourceId,
            report => string.Join("|", report.Blockers.Select(finding => finding.Key).Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);

        actual.Should().Equal(expected);
        reports.Where(report => report.IsReadyForSidecarDefault)
            .Select(report => report.SourceId)
            .Should()
            .Equal(ExpectedReadyRegistrationIds());
    }

    [Test]
    public void BundledRegistrationsOptedIntoSidecarHostModeMustBeReadinessClean()
    {
        var reports = AnalyzeBundledRegistrations()
            .ToDictionary(report => report.SourceId, StringComparer.Ordinal);
        var sidecarRegistrationIds = LoadBundledManifests()
            .Where(entry => entry.RuntimeInfo.IsSidecarHostMode)
            .Select(entry => entry.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (IsDeveloperConfiguration())
        {
            sidecarRegistrationIds.Should().Contain("sharpclaw_test_harness_out_of_process");
        }
        else
        {
            sidecarRegistrationIds.Should().NotContain("sharpclaw_test_harness_out_of_process");
        }
        foreach (var SourceId in sidecarRegistrationIds)
        {
            reports.Should().ContainKey(SourceId);
            reports[SourceId].Blockers.Should().BeEmpty(
                $"module '{SourceId}' opted into hostMode=sidecar and must stay protocol-ready");
        }
    }

    [Test]
    public void ReadinessCleanBundledRegistrationsMustDeclareSidecarHostMode()
    {
        var readyRegistrationIds = AnalyzeBundledRegistrations()
            .Where(report => report.IsReadyForSidecarDefault)
            .Select(report => report.SourceId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sidecarRegistrationIds = LoadBundledManifests()
            .Where(entry => entry.RuntimeInfo.IsSidecarHostMode)
            .Select(entry => entry.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        sidecarRegistrationIds.Should().Equal(
            readyRegistrationIds,
            "phase two should route every readiness-clean bundled module through the .NET sidecar manifest path");
    }

    [Test]
    public void BundledSidecarReadinessInventoryDistinguishesCoveredProtocolSurfaces()
    {
        var reports = AnalyzeBundledRegistrations().ToDictionary(report => report.SourceId, StringComparer.Ordinal);

        reports["sharpclaw_agent_orchestration"].Findings
            .Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "tools.job");

        if (IsDeveloperConfiguration())
        {
            reports["sharpclaw_test_harness_out_of_process"].Findings
                .Should()
                .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                    && finding.Key == "tools.inline")
                .And
                .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                    && finding.Key == "tools.job");
        }

        reports["sharpclaw_editor_common"].Findings
            .Should()
            .Contain(finding => finding.Kind == SidecarReadinessFindingKind.CoveredByCurrentProtocol
                                && finding.Key == "endpoints.http");
    }

    private static IReadOnlyList<RegistrationSidecarReadinessReport> AnalyzeBundledRegistrations()
    {
        var modules = LoadBundledRegistrations();
        var analyzer = new SidecarReadinessAnalyzer();
        return analyzer.AnalyzeAll(modules);
    }

    private static IReadOnlyList<ISharpClawCoreRegistration> LoadBundledRegistrations()
    {
        var apiOutputDir = ResolveApiOutputDirectory();
        var entryType = typeof(ISharpClawCoreRegistration);
        var modules = new List<ISharpClawCoreRegistration>();

        foreach (var dllName in ExpectedRegistrationDlls())
        {
            var dllPath = Path.Combine(apiOutputDir, dllName);
            File.Exists(dllPath).Should().BeTrue($"'{dllName}' must be present in API output");

            var assembly = Assembly.LoadFrom(dllPath);
            var implementations = assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                               && entryType.IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            implementations.Should().ContainSingle(
                $"'{dllName}' must contain exactly one public parameterless ISharpClawCoreRegistration implementation");

            modules.Add((ISharpClawCoreRegistration)Activator.CreateInstance(implementations[0])!);
        }

        return modules.OrderBy(module => module.Id, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ExpectedRegistrationDlls()
    {
        foreach (var dllName in ExpectedPackagedRegistrationDlls)
            yield return dllName;

        if (IsDeveloperConfiguration())
            yield return "SharpClaw.DefaultPackages.TestHarness.OutOfProcess.dll";
    }

    private static IReadOnlyDictionary<string, string[]> ExpectedBlockerKeys()
    {
        var expected = new Dictionary<string, string[]>(ExpectedPackagedBlockerKeys, StringComparer.Ordinal);
        if (IsDeveloperConfiguration())
            expected["sharpclaw_test_harness_out_of_process"] = [];

        return expected;
    }

    private static IEnumerable<string> ExpectedReadyRegistrationIds()
    {
        var expected = new List<string>
        {
            "sharpclaw_agent_orchestration",
            "sharpclaw_editor_common",
            "sharpclaw_metrics",
            "sharpclaw_registration_dev",
            "sharpclaw_providers_anthropic",
            "sharpclaw_providers_google",
            "sharpclaw_providers_llamasharp",
            "sharpclaw_providers_ollama",
            "sharpclaw_providers_openai_compat",
            "sharpclaw_vs2026_editor",
            "sharpclaw_vscode_editor",
        };

        if (IsDeveloperConfiguration())
            expected.Add("sharpclaw_test_harness_out_of_process");

        return expected.Order(StringComparer.Ordinal);
    }

    private static bool IsDeveloperConfiguration()
    {
        var testBinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var configuration = new DirectoryInfo(testBinDir).Parent!.Name;
        return string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveApiOutputDirectory()
    {
        var testBinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var solutionRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", ".."));
        var config = new DirectoryInfo(testBinDir).Parent!.Name;
        var tfm = new DirectoryInfo(testBinDir).Name;

        return Path.Combine(solutionRoot, "SharpClaw.Runtime", "Host", "bin", config, tfm);
    }

    private static IReadOnlyList<(PackageManifest Manifest, PackageRuntimeInfo RuntimeInfo)>
        LoadBundledManifests()
    {
        var registrationsDir = Path.Combine(ResolveApiOutputDirectory(), "contributions");
        Directory.Exists(registrationsDir).Should().BeTrue();

        return Directory.EnumerateFiles(registrationsDir, "package.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                var json = File.ReadAllText(path);
                var manifest = JsonSerializer.Deserialize<PackageManifest>(json, SecureJsonOptions.Manifest)!;
                return (Manifest: manifest, RuntimeInfo: PackageRuntimeInfo.FromJson(json));
            })
            .OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
