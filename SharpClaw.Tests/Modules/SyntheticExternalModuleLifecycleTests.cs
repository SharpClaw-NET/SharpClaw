using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Application.Core.Services.Triggers;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Tests.ExternalModule;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class SyntheticExternalModuleLifecycleTests
{
    [Test]
    public void ModuleManifestWithoutRuntimeDefaultsToDotNet()
    {
        const string json =
            """
            {
              "id": "synthetic_external_lifecycle",
              "displayName": "Synthetic External Lifecycle",
              "version": "1.0.0",
              "toolPrefix": "sel",
              "entryAssembly": "SharpClaw.Tests.ExternalModule.dll",
              "minHostVersion": "0.0.0"
            }
            """;
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(
            json,
            SecureJsonOptions.Manifest)!;
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);

        manifest.EntryAssembly.Should().Be("SharpClaw.Tests.ExternalModule.dll");
        runtimeInfo.Runtime.Should().Be(ModuleManifestRuntimeInfo.DotNet);
        runtimeInfo.IsDotNet.Should().BeTrue();
    }

    [Test]
    public async Task NuGetPackageWithForeignRuntimeFailsWithUnsupportedRuntime()
    {
        var packageSource = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "foreign-runtime-nuget-source",
            Guid.NewGuid().ToString("N"));
        var packageCache = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "foreign-runtime-nuget-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageSource);

        const string packageId = "SharpClaw.Tests.ForeignRuntime.Package";
        const string version = "1.0.0";
        CreateForeignRuntimeModulePackage(packageSource, packageId, version);

        var act = async () => await NuGetModulePackageResolver.ResolveAsync(
            new NuGetModulePackageReference(packageId, version, packageSource),
            packageCache);

        await act.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*runtime 'python'*only supports 'dotnet'*not implemented yet*");
    }

    [Test]
    public async Task NuGetPackageModuleMaterializesAndLoadsThroughSidecarModuleService()
    {
        await using var host = CreateSidecarHarness();
        var packageSource = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "nuget-module-source",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageSource);

        const string packageId = "SharpClaw.Tests.ExternalModule.Package";
        const string version = "1.0.0";
        CreateSyntheticExternalModulePackage(packageSource, packageId, version);
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var moduleService = host.Services.GetRequiredService<ModuleService>();

        try
        {
            var response = await moduleService.LoadExternalPackageAsync(
                new NuGetModulePackageReference(packageId, version, packageSource),
                host.RootServices,
                CancellationToken.None);

            response.ModuleId.Should().Be(SyntheticExternalLifecycleModule.ModuleId);
            registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeTrue();
            registry.GetModule(SyntheticExternalLifecycleModule.ModuleId).Should().NotBeNull();
            registry.GetRuntimeHost(SyntheticExternalLifecycleModule.ModuleId)
                .Should()
                .BeAssignableTo<IForeignModuleRuntimeHost>();
            registry.TryResolve(SyntheticExternalLifecycleModule.JobTool, out var moduleId, out var toolName)
                .Should().BeTrue();
            moduleId.Should().Be(SyntheticExternalLifecycleModule.ModuleId);
            toolName.Should().Be(SyntheticExternalLifecycleModule.JobTool);
        }
        finally
        {
            if (registry.GetModule(SyntheticExternalLifecycleModule.ModuleId) is not null)
                await moduleService.UnloadExternalAsync(SyntheticExternalLifecycleModule.ModuleId);
        }
    }

    [Test]
    public async Task ExternalModuleUnloadRemovesModuleOwnedSurfacesAndKeepsCoreState()
    {
        await using var host = CreateSidecarHarness();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var factory = host.Services.GetRequiredService<ProviderApiClientFactory>();
        var triggerRegistry = new TaskTriggerSourceRegistry([], moduleRegistry: registry);
        var moduleService = host.Services.GetRequiredService<ModuleService>();

        var moduleDir = CreateSyntheticExternalModuleDirectory();
        await moduleService.LoadExternalFromAbsolutePathAsync(
            moduleDir,
            host.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        try
        {
            registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeTrue();
            registry.GetRuntimeHost(SyntheticExternalLifecycleModule.ModuleId)
                .Should()
                .BeAssignableTo<IForeignModuleRuntimeHost>();
            registry.GetHeaderTag(SyntheticExternalLifecycleModule.HeaderTag).Should().NotBeNull();
            registry.IsInlineTool(SyntheticExternalLifecycleModule.InlineTool).Should().BeTrue();
            registry.TryResolve(SyntheticExternalLifecycleModule.JobTool, out var moduleId, out var toolName)
                .Should().BeTrue();
            moduleId.Should().Be(SyntheticExternalLifecycleModule.ModuleId);
            toolName.Should().Be(SyntheticExternalLifecycleModule.JobTool);
            registry.GetDescriptorByDefaultResourceKey(SyntheticExternalLifecycleModule.DefaultResourceKey)
                .Should().NotBeNull();

            factory.IsAvailable(SyntheticExternalLifecycleModule.ProviderKey).Should().BeTrue();
            var providerPlugin = factory.GetPlugin(SyntheticExternalLifecycleModule.ProviderKey);
            providerPlugin.Should().NotBeNull();
            providerPlugin!.CostFeed.Should().NotBeNull();
            triggerRegistry.ResolveByKey(SyntheticExternalLifecycleModule.TriggerKey).Should().NotBeNull();

            var seeded = await host.SeedChatAsync(
                SyntheticExternalLifecycleModule.ProviderKey,
                disableToolSchemas: true);
            seeded.Channel.CustomChatHeader = "core persisted header";
            await host.Db.SaveChangesAsync();

            var chat = await host.Chat.SendMessageAsync(
                seeded.Channel.Id,
                new ChatRequest("hello from persisted chat"));
            chat.AssistantMessage.Content.Should().Be(SyntheticExternalLifecycleModule.ChatText);

            var job = await host.Services.GetRequiredService<AgentJobService>()
                .SubmitAsync(
                    seeded.Channel.Id,
                    new SubmitAgentJobRequest(
                        ActionKey: SyntheticExternalLifecycleModule.JobTool,
                        ScriptJson: """{"value":"direct"}"""));
            job.Status.Should().Be(AgentJobStatus.Completed);
            job.ResultData.Should().Be("external job direct");

            var costs = await host.Services.GetRequiredService<ProviderCostService>()
                .GetCostAsync(
                    seeded.Provider.Id,
                    startDate: DateTimeOffset.UnixEpoch,
                    endDate: DateTimeOffset.UnixEpoch.AddDays(1));
            costs!.TotalCost.Should().Be(3.21m);

            await moduleService.UnloadExternalAsync(SyntheticExternalLifecycleModule.ModuleId);

            registry.GetModule(SyntheticExternalLifecycleModule.ModuleId).Should().BeNull();
            registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeFalse();
            registry.GetHeaderTag(SyntheticExternalLifecycleModule.HeaderTag).Should().BeNull();
            registry.IsInlineTool(SyntheticExternalLifecycleModule.InlineTool).Should().BeFalse();
            registry.TryResolve(SyntheticExternalLifecycleModule.JobTool, out _, out _).Should().BeFalse();
            registry.GetDescriptorByDefaultResourceKey(SyntheticExternalLifecycleModule.DefaultResourceKey)
                .Should().BeNull();
            factory.IsAvailable(SyntheticExternalLifecycleModule.ProviderKey).Should().BeFalse();
            factory.GetPlugin(SyntheticExternalLifecycleModule.ProviderKey).Should().BeNull();
            triggerRegistry.ResolveByKey(SyntheticExternalLifecycleModule.TriggerKey).Should().BeNull();

            registry.IsRegisteredDefaultResourceKey("agent").Should().BeTrue();
            seeded.Channel.CustomChatHeader.Should().Be("core persisted header");
            var messageCount = await host.Db.ChatMessages.CountAsync(m => m.ChannelId == seeded.Channel.Id);
            var jobCount = await host.Db.AgentJobs.CountAsync(
                j => j.Id == job.Id && j.ResultData == "external job direct");
            messageCount.Should().Be(2);
            jobCount.Should().Be(1);
        }
        finally
        {
            if (registry.GetModule(SyntheticExternalLifecycleModule.ModuleId) is not null)
                await moduleService.UnloadExternalAsync(SyntheticExternalLifecycleModule.ModuleId);
        }
    }

    private static string CreateSyntheticExternalModuleDirectory()
    {
        var assemblyPath = typeof(SyntheticExternalLifecycleModule).Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var moduleDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "external-modules",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(
            Path.Combine(moduleDir, "module.json"),
            SyntheticExternalManifestJson("1.0.0", Path.GetFileName(assemblyPath)));

        return moduleDir;
    }

    private static void CreateSyntheticExternalModulePackage(
        string packageSource,
        string packageId,
        string version)
    {
        var assemblyPath = typeof(SyntheticExternalLifecycleModule).Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var packagePath = Path.Combine(packageSource, $"{packageId}.{version}.nupkg");

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(
            archive,
            "module.json",
            SyntheticExternalManifestJson(version, Path.GetFileName(assemblyPath)));
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
                <description>Synthetic SharpClaw module package.</description>
              </metadata>
            </package>
            """);

        foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
            archive.CreateEntryFromFile(file, Path.GetFileName(file));

        foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
            archive.CreateEntryFromFile(file, Path.GetFileName(file));
    }

    private static void CreateForeignRuntimeModulePackage(
        string packageSource,
        string packageId,
        string version)
    {
        var packagePath = Path.Combine(packageSource, $"{packageId}.{version}.nupkg");

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(
            archive,
            "sharpclaw/module.json",
            """
            {
              "id": "synthetic_python_module",
              "displayName": "Synthetic Python Module",
              "version": "1.0.0",
              "toolPrefix": "spm",
              "runtime": "python",
              "entrypoint": "synthetic_module.main:app",
              "minHostVersion": "0.0.0"
            }
            """);
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
                <description>Synthetic foreign-runtime SharpClaw module package.</description>
              </metadata>
            </package>
            """);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static ChatHarnessHost CreateSidecarHarness() =>
        ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Modules:DotNetSidecarHostPath"] = ResolveDotNetSidecarHostPath(),
        });

    private static string SyntheticExternalManifestJson(string version, string entryAssembly) =>
        $$"""
        {
          "id": "{{SyntheticExternalLifecycleModule.ModuleId}}",
          "displayName": "Synthetic External Lifecycle",
          "version": "{{version}}",
          "toolPrefix": "{{SyntheticExternalLifecycleModule.ToolPrefixValue}}",
          "runtime": "dotnet",
          "hostMode": "sidecar",
          "entryAssembly": "{{entryAssembly}}",
          "moduleType": "{{typeof(SyntheticExternalLifecycleModule).FullName}}",
          "minHostVersion": "0.0.0"
        }
        """;

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
}
