using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules;
using SharpClaw.Tests.ExternalModule;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class DotNetSidecarHostTests
{
    [Test]
    public void ModuleManifestRuntimeInfoReadsDotNetSidecarPackageMetadata()
    {
        var json = SidecarManifestJson("SharpClaw.Tests.ExternalModule.dll");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)!;
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        manifest.Id.Should().Be(DotNetSidecarFixtureModule.ModuleId);
        runtimeInfo.Runtime.Should().Be(ModuleManifestRuntimeInfo.DotNet);
        runtimeInfo.ModuleType.Should().Be(typeof(DotNetSidecarFixtureModule).FullName);
        runtimeInfo.IsSidecarHostMode.Should().BeTrue();
    }

    [Test]
    public async Task NuGetPackageCanDeclareDotNetSidecarModuleType()
    {
        using var workspace = TestWorkspace.Create();
        const string packageId = "SharpClaw.Tests.DotNetSidecar.Package";
        const string version = "1.0.0";
        Directory.CreateDirectory(workspace.PackageSourceDir);
        CreateSidecarPackage(workspace.PackageSourceDir, packageId, version);

        var moduleDir = await NuGetModulePackageResolver.ResolveAsync(
            new NuGetModulePackageReference(packageId, version, workspace.PackageSourceDir),
            workspace.PackageCacheDir);
        var json = await File.ReadAllTextAsync(Path.Combine(moduleDir, "module.json"));
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, SecureJsonOptions.Manifest)!;
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        manifest.Id.Should().Be(DotNetSidecarFixtureModule.ModuleId);
        runtimeInfo.IsSidecarHostMode.Should().BeTrue();
        runtimeInfo.ModuleType.Should().Be(typeof(DotNetSidecarFixtureModule).FullName);
        File.Exists(Path.Combine(moduleDir, manifest.EntryAssembly)).Should().BeTrue();
    }

    [Test]
    public async Task DotNetSidecarHostAdaptsSharpClawModuleToForeignProtocol()
    {
        using var workspace = TestWorkspace.Create();
        CopyFixtureModulePayload(workspace.ModuleDir);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.ModuleDir, "module.json"),
            SidecarManifestJson("SharpClaw.Tests.ExternalModule.dll"));

        var manifest = JsonSerializer.Deserialize<ModuleManifest>(
            await File.ReadAllTextAsync(Path.Combine(workspace.ModuleDir, "module.json")),
            SecureJsonOptions.Manifest)!;
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(
            await File.ReadAllTextAsync(Path.Combine(workspace.ModuleDir, "module.json")));
        await using var hostServices = new ServiceCollection()
            .AddSingleton<IModuleConfigStore, RecordingConfigStore>()
            .BuildServiceProvider();
        await using var foreignHost = await ForeignModuleHost.StartAsync(
            manifest,
            runtimeInfo,
            CreateLaunchOptions(workspace, hostServices));

        foreignHost.Handshake.Runtime.Should().Be(ModuleManifestRuntimeInfo.DotNet);
        foreignHost.Endpoints.Should().Contain(endpoint =>
            endpoint.Method == "GET"
            && endpoint.RoutePattern == "/modules/dotnet-sidecar/ping");
        foreignHost.Module.GetToolDefinitions()
            .Should()
            .ContainSingle(tool => tool.Name == DotNetSidecarFixtureModule.JobTool);
        foreignHost.Module.GetInlineToolDefinitions()
            .Should()
            .ContainSingle(tool => tool.Name == DotNetSidecarFixtureModule.InlineTool);

        using var payload = JsonDocument.Parse("""{"value":"hello"}""");
        var result = await foreignHost.Module.ExecuteToolAsync(
            DotNetSidecarFixtureModule.JobTool,
            payload.RootElement,
            new AgentJobContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, DotNetSidecarFixtureModule.JobTool),
            foreignHost.Services,
            CancellationToken.None);
        result.Should().Be("dotnet sidecar hello");

        var tag = foreignHost.Module.GetHeaderTags()!.Single();
        (await tag.Resolve(foreignHost.Services, CancellationToken.None)).Should().Be("hello");

        using var response = await foreignHost.SendEndpointRequestAsync(
            new HttpRequestMessage(HttpMethod.Get, "/modules/dotnet-sidecar/ping"),
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("dotnet sidecar pong");
    }

    private static ForeignModuleHostLaunchOptions CreateLaunchOptions(
        TestWorkspace workspace,
        IServiceProvider hostServices)
    {
        var hostPath = ResolveDotNetSidecarHostPath();
        return new ForeignModuleHostLaunchOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [hostPath],
            WorkingDirectory = Path.GetDirectoryName(hostPath),
            ModuleDirectory = workspace.ModuleDir,
            ModuleDataDirectory = workspace.DataDir,
            ControlAddress = new Uri($"http://127.0.0.1:{GetFreeTcpPort()}"),
            ControlToken = "dotnet-sidecar-token",
            StartupTimeout = TimeSpan.FromSeconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            HostVersion = "0.1.0-beta",
            HostServices = hostServices,
        };
    }

    private static string SidecarManifestJson(string entryAssembly) =>
        $$"""
        {
          "id": "{{DotNetSidecarFixtureModule.ModuleId}}",
          "displayName": "Synthetic .NET Sidecar",
          "version": "1.0.0",
          "toolPrefix": "{{DotNetSidecarFixtureModule.ToolPrefixValue}}",
          "runtime": "dotnet",
          "hostMode": "sidecar",
          "entryAssembly": "{{entryAssembly}}",
          "moduleType": "{{typeof(DotNetSidecarFixtureModule).FullName}}",
          "minHostVersion": "0.0.0"
        }
        """;

    private static void CreateSidecarPackage(string packageSource, string packageId, string version)
    {
        var packagePath = Path.Combine(packageSource, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(archive, "module.json", SidecarManifestJson("SharpClaw.Tests.ExternalModule.dll"));
        WriteTextEntry(
            archive,
            $"{packageId}.nuspec",
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>SharpClaw.Tests</authors>
                <description>Synthetic SharpClaw .NET sidecar module package.</description>
              </metadata>
            </package>
            """);

        foreach (var file in FixturePayloadFiles())
            archive.CreateEntryFromFile(file, Path.GetFileName(file));
    }

    private static void CopyFixtureModulePayload(string moduleDir)
    {
        foreach (var file in FixturePayloadFiles())
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);
    }

    private static IEnumerable<string> FixturePayloadFiles()
    {
        var sourceDir = Path.GetDirectoryName(typeof(DotNetSidecarFixtureModule).Assembly.Location)!;
        foreach (var file in Directory.GetFiles(sourceDir, "SharpClaw.Tests.ExternalModule.*"))
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static string ResolveDotNetSidecarHostPath()
    {
        var root = ResolveRepoRoot();
        var configuration = Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name;
        var hostPath = Path.Combine(
            root,
            "SharpClaw.Modules.DotNetSidecarHost",
            "bin",
            configuration,
            "net10.0",
            "SharpClaw.Modules.DotNetSidecarHost.dll");

        File.Exists(hostPath).Should().BeTrue(
            $"shared .NET sidecar host must be built before tests run: '{hostPath}'");
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

    private sealed class RecordingConfigStore : IModuleConfigStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            where T : IParsable<T>
        {
            var value = _values.GetValueOrDefault(key);
            return Task.FromResult(
                value is not null && T.TryParse(value, null, out var parsed)
                    ? parsed
                    : default);
        }

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(_values, StringComparer.Ordinal));
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            ModuleDir = Path.Combine(root, "module");
            DataDir = Path.Combine(root, "data");
            PackageSourceDir = Path.Combine(root, "packages");
            PackageCacheDir = Path.Combine(root, "package-cache");
            Directory.CreateDirectory(ModuleDir);
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string ModuleDir { get; }
        public string DataDir { get; }
        public string PackageSourceDir { get; }
        public string PackageCacheDir { get; }

        public static TestWorkspace Create() =>
            new(Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "dotnet-sidecar",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
