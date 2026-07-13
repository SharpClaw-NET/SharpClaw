using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledTestHarnessSidecarConformanceTests
{
    [Test]
    public async Task TestHarnessRunsThroughDotNetSidecarProtocol()
    {
        using var workspace = TestWorkspace.Create();
        CopyTestHarnessPayload(workspace.ModuleDir);
        var json = await File.ReadAllTextAsync(Path.Combine(workspace.ModuleDir, "module.json"));
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)!;
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        runtimeInfo.IsSidecarHostMode.Should().BeTrue();
        runtimeInfo.ModuleType.Should().Be("SharpClaw.DefaultModules.TestHarness.TestHarnessOutOfProcessModule");

        await using var foreignHost = await ForeignModuleHost.StartAsync(
            manifest,
            runtimeInfo,
            CreateLaunchOptions(workspace));

        foreignHost.Handshake.Runtime.Should().Be(ModuleManifestRuntimeInfo.DotNet);
        foreignHost.Module.GetGlobalFlagDescriptors()
            .Should()
            .ContainSingle(flag => flag.FlagKey == TestHarnessConstants.GlobalFlagKey);
        foreignHost.Module.GetResourceTypeDescriptors()
            .Should()
            .ContainSingle(resource => resource.ResourceType == TestHarnessConstants.ResourceType);
        foreignHost.Module.GetHeaderTags()
            .Should()
            .ContainSingle(tag => tag.Name == TestHarnessConstants.HeaderTagName);

        var permissionedTool = foreignHost.Module.GetToolDefinitions()
            .Single(tool => tool.Name == TestHarnessConstants.JobPermissionedTool);
        permissionedTool.Permission.DelegateTo.Should().Be(TestHarnessConstants.DelegateName);

        var resourceTool = foreignHost.Module.GetToolDefinitions()
            .Single(tool => tool.Name == TestHarnessConstants.JobResourceTool);
        resourceTool.Permission.IsPerResource.Should().BeTrue();
        resourceTool.Permission.DelegateTo.Should().Be(TestHarnessConstants.ResourceDelegateName);

        using var parameters = JsonDocument.Parse("""{"result":"sidecar job","remainExecuting":true}""");
        var job = new AgentJobContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ResourceId: null,
            ActionKey: TestHarnessConstants.JobPermissionedTool);
        foreignHost.Module.GetJobCompletionBehavior(
                TestHarnessConstants.JobPermissionedTool,
                parameters.RootElement,
                job)
            .Should()
            .Be(ModuleJobCompletionBehavior.RemainExecuting);

        var jobResult = await foreignHost.Module.ExecuteToolAsync(
            TestHarnessConstants.JobPermissionedTool,
            parameters.RootElement,
            job,
            foreignHost.Services,
            CancellationToken.None);
        jobResult.Should().Be("sidecar job");

        using var emptyParameters = JsonDocument.Parse("""{}""");
        var inlineResult = await foreignHost.Module.ExecuteInlineToolAsync(
            TestHarnessConstants.InlineOpenTool,
            emptyParameters.RootElement,
            new InlineToolContext(Guid.NewGuid(), Guid.NewGuid(), null, "call-1"),
            foreignHost.Services,
            CancellationToken.None);
        inlineResult.Should().Be("test harness tool result");

        var stream = foreignHost.Module.ExecuteToolStreamingAsync(
            TestHarnessConstants.JobStreamingTool,
            parameters.RootElement,
            job,
            foreignHost.Services,
            CancellationToken.None);
        stream.Should().NotBeNull();
        var chunks = new List<string>();
        await foreach (var chunk in stream!)
            chunks.Add(chunk);
        string.Concat(chunks).Should().Be("sidecar job");

        var header = foreignHost.Module.GetHeaderTags()!.Single();
        (await header.Resolve(foreignHost.Services, CancellationToken.None))
            .Should()
            .Be("test harness header tag");

        var resource = foreignHost.Module.GetResourceTypeDescriptors().Single();
        (await resource.LoadAllIds(foreignHost.Services, CancellationToken.None))
            .Should()
            .BeEmpty();

        var providers = foreignHost.Services.GetServices<IProviderPlugin>().ToArray();
        providers.Select(provider => provider.ProviderKey)
            .Should()
            .Contain([
                TestHarnessConstants.PlainProviderKey,
                TestHarnessConstants.StreamingProviderKey,
                TestHarnessConstants.ToolProviderKey,
                TestHarnessConstants.CostProviderKey,
            ]);

        var providerPlugin = providers.Single(provider =>
            provider.ProviderKey == TestHarnessConstants.PlainProviderKey);
        var providerResult = await providerPlugin
            .CreateClient(new ProviderClientOptions(null))
            .ChatCompletionAsync(
            model: TestHarnessConstants.ModelId,
            systemPrompt: "system",
            messages: [new ChatCompletionMessage("user", "hello")],
            ct: CancellationToken.None);
        providerResult.Content.Should().Be("test harness response");

        var costPlugin = providers.Single(provider =>
            provider.ProviderKey == TestHarnessConstants.CostProviderKey);
        costPlugin.SupportsCostFeed.Should().BeTrue();
        var costFeed = costPlugin.CreateCostFeed(new ProviderClientOptions(null));
        costFeed.Should().NotBeNull();
        var cost = await costFeed!.GetCostsAsync(
            startTime: DateTimeOffset.UnixEpoch,
            endTime: DateTimeOffset.UnixEpoch.AddDays(1));
        cost!.TotalAmount.Should().Be(0.25m);
    }

    private static ForeignModuleHostLaunchOptions CreateLaunchOptions(TestWorkspace workspace)
    {
        var hostPath = ResolveOutOfProcessModuleHostPath();
        return new ForeignModuleHostLaunchOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [hostPath],
            WorkingDirectory = Path.GetDirectoryName(hostPath),
            ModuleDirectory = workspace.ModuleDir,
            ModuleDataDirectory = workspace.DataDir,
            ControlAddress = new Uri($"http://127.0.0.1:{GetFreeTcpPort()}"),
            ControlToken = "test-harness-sidecar-token",
            StartupTimeout = TimeSpan.FromSeconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            HostVersion = "0.1.0-beta",
        };
    }

    private static void CopyTestHarnessPayload(string moduleDir)
    {
        var sourceDir = TestContext.CurrentContext.TestDirectory;
        foreach (var file in Directory.GetFiles(sourceDir, "SharpClaw.DefaultModules.TestHarness.OutOfProcess.*"))
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);
            }
        }

        File.Copy(
            Path.Combine(
                ResolveRepoRoot(),
                "SharpClaw.DefaultModules",
                "TestHarness.OutOfProcess",
                "module.json"),
            Path.Combine(moduleDir, "module.json"),
            overwrite: true);
    }

    private static string ResolveOutOfProcessModuleHostPath()
    {
        var hostPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "SharpClaw.ModuleHost.OutOfProcess.dll");

        File.Exists(hostPath).Should().BeTrue(
            $"shared .NET sidecar host package payload must be copied to test output before tests run: '{hostPath}'");
        return hostPath;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            ModuleDir = Path.Combine(root, "module");
            DataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(ModuleDir);
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string ModuleDir { get; }
        public string DataDir { get; }

        public static TestWorkspace Create() =>
            new(Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "test-harness-sidecar",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
