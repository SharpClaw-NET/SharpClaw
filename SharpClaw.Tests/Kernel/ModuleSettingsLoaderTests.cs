using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Persistence;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using SharpClaw.TestFixtures.ExternalRegistration;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class ModuleSettingsLoaderTests
{
    [TestCase("in-process")]
    [TestCase("sidecar")]
    [NonParallelizable]
    public async Task ImportedIndependentPackageUsesProductionLoaderAndItsOwnSettingsEndpoints(string mode)
    {
        using var workspace = new Workspace();
        var source = workspace.CreatePayload(mode);
        var archive = workspace.Path("fixture.nupkg");
        File.WriteAllText(System.IO.Path.Combine(source, "Fixture.nuspec"), """
            <?xml version="1.0"?><package><metadata><id>External.Frontend</id><version>1.0.0</version></metadata></package>
            """);
        ZipFile.CreateFromDirectory(source, archive);
        using var store = new ModulePackageStore(workspace.Path("store"), new ModulePackageSources());
        using var candidate = await store.PrepareAsync(new("fixture.nupkg", LocalPath: archive), null, default);
        await store.CommitAsync(candidate, workspace.Path("empty"), _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask, default);
        var bundledRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "contributions");
        const string storageId = "sharpclaw_persistence_jsoncoldstore";
        var settings = new Dictionary<string, string?>
        {
            ["ExternalRegistrations:frontend-modules:Enabled"] = "true",
            ["ExternalRegistrations:frontend-modules:Path"] = store.ActiveRoot,
        };
        foreach (var manifest in Directory.EnumerateFiles(bundledRoot, "package.json", SearchOption.AllDirectories))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var id = json.RootElement.GetProperty("id").GetString()!;
            settings[$"Packages:{id}"] = (id == storageId).ToString();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var roots = PackagedRegistrationRootResolver.Resolve(bundledRoot, configuration);
        await using var loaded = await PackagedDotNetRegistrationSet.LoadProductionAsync(roots, configuration);
        loaded.SourceIds.Should().BeEquivalentTo([FrontendSettingsFixtureModule.SourceId, storageId]);
        var page = loaded.FrontendSettings.Single();
        page.SourceId.Should().Be(FrontendSettingsFixtureModule.SourceId);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        RuntimeHostComposition.RegisterServices(builder.Services, configuration,
            new(SharpClawInstanceKind.Backend, workspace.Path("instance"), workspace.Path("shared")),
            new EncryptionOptions { Key = new byte[32] },
            new SharpClawPersistenceOptions { DataDirectory = workspace.Path("database") }, loaded.Services);
        await using var app = builder.Build();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await loaded.ConnectCapabilitiesAsync(app.Services);
        await adapter.StartAsync("frontend-settings-fixture");
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        readiness.MarkReady();
        app.UseMiddleware<ApiKeyMiddleware>();
        KernelHostEndpoints.MapModuleSettingsCatalog(app, loaded);
        loaded.Application.MapEndpoints(app, adapter);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var locked = await http.GetAsync("/setup/modules");
            ((int)locked.StatusCode).Should().Be(423);
            await using var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
                new ClientActionDispatcher(), fixedApiKey: app.Services.GetRequiredService<ApiKeyProvider>().ApiKey);
            var catalog = await ModuleSettingsClient.ReadPagesAsync(api, default);
            catalog.Should().Equal(page);
            var document = await ModuleSettingsClient.ReadDocumentAsync(api, page, default);
            document.Values["message"].GetString().Should().Be("original");
            await ModuleSettingsClient.SaveAsync(api, page, new Dictionary<string, object?> { ["message"] = "changed" }, default);
            var changed = await ModuleSettingsClient.ReadDocumentAsync(api, page, default);
            changed.Values["message"].GetString().Should().Be("changed");
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    [Test]
    public void DisabledModuleCannotPreventRecoveryWithUnsupportedSettingsMetadata()
    {
        using var workspace = new Workspace();
        var source = workspace.CreatePayload("in-process");
        var path = System.IO.Path.Combine(source, "package.json");
        var json = File.ReadAllText(path).Replace("\"schemaVersion\":1", "\"schemaVersion\":999", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [$"Packages:{FrontendSettingsFixtureModule.SourceId}"] = "false" }).Build();
        using var loaded = PackagedDotNetRegistrationSet.Load(workspace.Path("packages"), configuration);
        loaded.SourceIds.Should().BeEmpty();
        loaded.FrontendSettings.Should().BeEmpty();
    }

    [Test]
    public void ManifestCannotBorrowAnotherModulesCompiledEndpoints()
    {
        using var workspace = new Workspace();
        workspace.CreatePayload("in-process");
        workspace.CreatePayload("in-process", empty: true);
        var act = () => PackagedDotNetRegistrationSet.Load(workspace.Path("packages"), new ConfigurationBuilder().Build());
        act.Should().Throw<InvalidOperationException>().WithMessage("*must use its own compiled GET endpoint*");
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpclaw-settings-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public string CreatePayload(string mode, bool empty = false)
        {
            var source = Path("packages/" + (empty ? "empty" : "fixture"));
            Directory.CreateDirectory(source);
            var assembly = typeof(FrontendSettingsFixtureModule).Assembly.Location;
            File.Copy(assembly, System.IO.Path.Combine(source, System.IO.Path.GetFileName(assembly)));
            var deps = System.IO.Path.ChangeExtension(assembly, ".deps.json");
            if (File.Exists(deps)) File.Copy(deps, System.IO.Path.Combine(source, System.IO.Path.GetFileName(deps)));
            File.WriteAllText(System.IO.Path.Combine(source, "package.json"), JsonSerializer.Serialize(new
            {
                id = empty ? "empty_frontend_settings" : FrontendSettingsFixtureModule.SourceId,
                displayName = empty ? "Empty frontend settings" : "External frontend settings",
                version = "1.0.0", toolPrefix = empty ? "efs" : "sfs", enabled = true,
                entryAssembly = System.IO.Path.GetFileName(assembly),
                entryType = empty ? typeof(EmptyFrontendSettingsFixtureModule).FullName : typeof(FrontendSettingsFixtureModule).FullName,
                runtime = "dotnet", hostMode = mode, minHostVersion = "0.1.0",
                frontend = new { schemaVersion = 1, settings = new[] {
                    new { id = "preferences", title = "Preferences", readPath = "/fixture/settings", savePath = "/fixture/settings" } } },
            }));
            return source;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
