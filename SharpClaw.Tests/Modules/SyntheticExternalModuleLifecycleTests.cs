using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
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
    public async Task NuGetPackageModuleMaterializesAndLoadsThroughExternalModuleHost()
    {
        await using var host = ChatHarnessHost.Create();
        var packageSource = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "nuget-module-source",
            Guid.NewGuid().ToString("N"));
        var packageCache = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "nuget-module-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageSource);

        const string packageId = "SharpClaw.Tests.ExternalModule.Package";
        const string version = "1.0.0";
        CreateSyntheticExternalModulePackage(packageSource, packageId, version);
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var moduleDir = await NuGetModulePackageResolver.ResolveAsync(
            new NuGetModulePackageReference(packageId, version, packageSource),
            packageCache);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(
            await File.ReadAllTextAsync(Path.Combine(moduleDir, "module.json")),
            SecureJsonOptions.Manifest)!;
        var moduleHost = ExternalModuleHost.Load(
            moduleDir,
            manifest,
            host.Services,
            host.Services.GetRequiredService<ILoggerFactory>());

        try
        {
            registry.Register(moduleHost.Module, moduleHost);
            await moduleHost.Module.InitializeAsync(moduleHost.Services, CancellationToken.None);

            registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeTrue();
            registry.GetModule(SyntheticExternalLifecycleModule.ModuleId).Should().NotBeNull();
            registry.TryResolve(SyntheticExternalLifecycleModule.JobTool, out var moduleId, out var toolName)
                .Should().BeTrue();
            moduleId.Should().Be(SyntheticExternalLifecycleModule.ModuleId);
            toolName.Should().Be(SyntheticExternalLifecycleModule.JobTool);
        }
        finally
        {
            if (registry.GetModule(SyntheticExternalLifecycleModule.ModuleId) is not null)
            {
                await moduleHost.DrainAsync(TimeSpan.FromSeconds(1));
                await moduleHost.Module.ShutdownAsync();
                registry.Unregister(SyntheticExternalLifecycleModule.ModuleId);
            }

            await moduleHost.DisposeAsync();
        }
    }

    [Test]
    public async Task ExternalModuleUnloadRemovesModuleOwnedSurfacesAndKeepsCoreState()
    {
        await using var host = ChatHarnessHost.Create();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var factory = host.Services.GetRequiredService<ProviderApiClientFactory>();
        var triggerRegistry = new TaskTriggerSourceRegistry([], moduleRegistry: registry);

        var moduleHost = LoadSyntheticExternalModule(host.Services);
        registry.Register(moduleHost.Module, moduleHost);
        await moduleHost.Module.InitializeAsync(moduleHost.Services, CancellationToken.None);

        try
        {
            registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeTrue();
            registry.GetExternalHost(SyntheticExternalLifecycleModule.ModuleId).Should().BeSameAs(moduleHost);
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

            await moduleHost.DrainAsync(TimeSpan.FromSeconds(1));
            await moduleHost.Module.ShutdownAsync();
            registry.Unregister(SyntheticExternalLifecycleModule.ModuleId);
            await moduleHost.DisposeAsync();

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
            {
                await moduleHost.DrainAsync(TimeSpan.FromSeconds(1));
                await moduleHost.Module.ShutdownAsync();
                registry.Unregister(SyntheticExternalLifecycleModule.ModuleId);
                await moduleHost.DisposeAsync();
            }
        }
    }

    private static ExternalModuleHost LoadSyntheticExternalModule(IServiceProvider hostServices)
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

        var manifest = new ModuleManifest(
            SyntheticExternalLifecycleModule.ModuleId,
            "Synthetic External Lifecycle",
            "1.0.0",
            SyntheticExternalLifecycleModule.ToolPrefixValue,
            Path.GetFileName(assemblyPath),
            "0.0.0");
        File.WriteAllText(
            Path.Combine(moduleDir, "module.json"),
            JsonSerializer.Serialize(manifest));

        return ExternalModuleHost.Load(
            moduleDir,
            manifest,
            hostServices,
            hostServices.GetRequiredService<ILoggerFactory>());
    }

    private static void CreateSyntheticExternalModulePackage(
        string packageSource,
        string packageId,
        string version)
    {
        var assemblyPath = typeof(SyntheticExternalLifecycleModule).Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var packagePath = Path.Combine(packageSource, $"{packageId}.{version}.nupkg");
        var manifest = new ModuleManifest(
            SyntheticExternalLifecycleModule.ModuleId,
            "Synthetic External Lifecycle",
            version,
            SyntheticExternalLifecycleModule.ToolPrefixValue,
            Path.GetFileName(assemblyPath),
            "0.0.0");

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteTextEntry(archive, "module.json", JsonSerializer.Serialize(manifest));
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

    private static void WriteTextEntry(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
