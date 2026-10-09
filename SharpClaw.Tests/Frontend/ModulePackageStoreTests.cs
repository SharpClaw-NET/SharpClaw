using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
public sealed class ModulePackageStoreTests
{
    [TestCase("../escape.dll")]
    [TestCase("/escape.dll")]
    [TestCase("C:/escape.dll")]
    [TestCase("a\\escape.dll")]
    [TestCase("a/../escape.dll")]
    [TestCase("a//escape.dll")]
    [TestCase("a/NUL.dll")]
    [TestCase("a/file.dll:stream")]
    [TestCase("a/file.dll.")]
    public void UnsafePathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => ModulePackageStore.RequireRelativePath(path));

    [Test]
    public async Task LocalPayloadIsCopiedWithoutExecutingAndExplicitCommitUsesOneExternalRoot()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var source = workspace.CreatePayload();
        using var inspected = await store.PrepareAsync(new("local", LocalPath: source), null, default);
        inspected.Modules.Single().Id.Should().Be("frontend_fixture");
        store.ReadInstalled().Should().BeEmpty();
        var stopped = false;
        string? configured = null;
        await store.CommitAsync(inspected, workspace.Path("bundled"), _ =>
        { stopped = true; return Task.CompletedTask; }, (root, modules, _) =>
        {
            stopped.Should().BeTrue();
            configured = root;
            modules.Should().Equal(inspected.Modules);
            return Task.CompletedTask;
        }, default);
        configured.Should().Be(store.ActiveRoot);
        store.ReadInstalled().Should().Equal(inspected.Modules);
        File.ReadAllText(Directory.GetFiles(store.ActiveRoot, "LICENSE.md", SearchOption.AllDirectories).Single())
            .Should().Be("fixture legal notice");
    }

    [Test]
    public async Task ModifiedInspectedBytesCannotCommitOrStopRuntime()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        using var inspected = await store.PrepareAsync(new("local", LocalPath: workspace.CreatePayload()), null, default);
        await File.AppendAllTextAsync(System.IO.Path.Combine(inspected.Root, "payload", "LICENSE.md"), "changed");
        var stopped = false;
        Func<Task> install = () => store.CommitAsync(inspected, workspace.Path("bundled"), _ =>
        { stopped = true; return Task.CompletedTask; }, (_, _, _) => Task.CompletedTask, default);
        await install.Should().ThrowAsync<InvalidDataException>();
        stopped.Should().BeFalse();
        store.ReadInstalled().Should().BeEmpty();
    }

    [Test]
    public async Task ExistingIdentityIsNotOverwritten()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var source = workspace.CreatePayload();
        using var first = await store.PrepareAsync(new("local", LocalPath: source), null, default);
        await store.CommitAsync(first, workspace.Path("bundled"), _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask, default);
        using var second = await store.PrepareAsync(new("local", LocalPath: source), null, default);
        Func<Task> install = () => store.CommitAsync(second, workspace.Path("bundled"), _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask, default);
        await install.Should().ThrowAsync<InvalidDataException>();
        store.ReadInstalled().Should().HaveCount(1);
    }

    [Test]
    public async Task FailedConfigurationRemovesOnlyNewDestination()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        using var inspected = await store.PrepareAsync(new("local", LocalPath: workspace.CreatePayload()), null, default);
        Func<Task> install = () => store.CommitAsync(inspected, workspace.Path("bundled"), _ => Task.CompletedTask,
            (_, _, _) => throw new InvalidOperationException("test failure"), default);
        await install.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetDirectories(store.ActiveRoot).Should().BeEmpty();
        File.Exists(System.IO.Path.Combine(inspected.Root, "payload", "package.json")).Should().BeFalse();
    }

    [Test]
    public async Task ArchiveTraversalCollisionAndLinksLeaveNoScratch()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        foreach (var shape in new[] { "traversal", "collision", "link" })
        {
            var path = workspace.Path(shape + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(shape == "traversal" ? "../escape.dll" : "entry.dll");
                if (shape == "link") entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                if (shape == "collision") archive.CreateEntry("ENTRY.dll");
            }
            Func<Task> inspect = async () => { using var candidate = await store.PrepareAsync(new(shape, LocalPath: path), null, default); };
            await inspect.Should().ThrowAsync<InvalidDataException>();
            Directory.GetDirectories(workspace.Path("store"), "inspection-*").Should().BeEmpty();
        }
    }

    [Test]
    public async Task LocalLinkIsNeverFollowed()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var source = workspace.CreatePayload();
        if (OperatingSystem.IsWindows())
        {
            var target = workspace.Path("link-target");
            Directory.CreateDirectory(target);
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", System.IO.Path.Combine(source, "linked"), target })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, "directory junction creation needs no developer-mode privilege");
        }
        else File.CreateSymbolicLink(System.IO.Path.Combine(source, "linked.txt"), System.IO.Path.Combine(source, "LICENSE.md"));
        Func<Task> inspect = async () => { using var candidate = await store.PrepareAsync(new("local", LocalPath: source), null, default); };
        await inspect.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task NuGetGalleryAndDownloadLinksSelectExactFeedIdentity()
    {
        using var sources = new ModulePackageSources(new HttpClient(new Handler(request =>
        {
            request.Headers.Authorization.Should().BeNull("NuGet discovery must not reuse a GitHub credential");
            var json = request.RequestUri!.AbsolutePath.EndsWith("/v3/index.json", StringComparison.Ordinal)
                ? """{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://api.nuget.org/v3-flatcontainer/"}]}"""
                : """{"versions":["1.0.0","2.0.0-beta.1","2.0.0"]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        })));
        foreach (var link in new[] { "https://www.nuget.org/packages/Example.Module/1.0.0",
            "https://www.nuget.org/api/v2/package/Example.Module/1.0.0" })
        {
            var choice = (await sources.ResolveAsync(link, "not-a-nuget-credential", default)).Single();
            choice.PackageId.Should().Be("Example.Module");
            choice.Version.Should().Be("1.0.0");
            choice.Download.Should().Be(new Uri("https://api.nuget.org/v3-flatcontainer/example.module/1.0.0/example.module.1.0.0.nupkg"));
        }
        var versions = await sources.ResolveAsync("https://www.nuget.org/packages/Example.Module", null, default);
        versions.Select(item => item.Version).Should().Equal("2.0.0", "1.0.0", "2.0.0-beta.1");
    }

    [Test]
    public async Task GitHubPackageCredentialsAreExplicitAndDroppedEvenOnRedirectBackToSource()
    {
        var requests = new List<(string Host, string? Authorization)>();
        using var sources = new ModulePackageSources(new HttpClient(new Handler(request =>
        {
            requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.Scheme));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/user")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"login":"token-holder"}""") };
            if (path.EndsWith("/42", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"name":"1.0.0"}""") };
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(path == "/owner/index.json"
                    ? """{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://nuget.pkg.github.com/owner/flat/"}]}"""
                    : """{"versions":["1.0.0"]}""") };
            if (request.RequestUri.Host == "nuget.pkg.github.com" && path.EndsWith(".nupkg", StringComparison.Ordinal))
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://objects.githubusercontent.com/asset.zip") } };
            if (request.RequestUri.Host == "objects.githubusercontent.com")
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://nuget.pkg.github.com/owner/final.zip") } };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        })));
        var choice = (await sources.ResolveAsync("https://github.com/owner/repo/pkgs/nuget/Example.Module/42", "explicit-token", default)).Single();
        using var output = new MemoryStream();
        choice.GitHubUsername.Should().Be("token-holder", "the feed namespace is not the credential owner");
        await sources.DownloadAsync(choice, output, "explicit-token", default);
        requests[0].Should().Be(("api.github.com", "Bearer"));
        requests[1].Should().Be(("api.github.com", "Bearer"));
        requests[2].Should().Be(("nuget.pkg.github.com", "Basic"));
        requests[^2].Should().Be(("objects.githubusercontent.com", null));
        requests[^1].Should().Be(("nuget.pkg.github.com", null));
        output.ToArray().Should().Equal(1, 2, 3);
    }

    [Test]
    public async Task NupkgMissingOrMismatchedIdentityCannotBecomeAnInspectedCandidate()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var source = workspace.CreatePayload();
        var noSpec = workspace.Path("missing.nupkg");
        ZipFile.CreateFromDirectory(source, noSpec);
        Func<Task> missing = async () => { using var _ = await store.PrepareAsync(new("missing.nupkg", LocalPath: noSpec), null, default); };
        await missing.Should().ThrowAsync<InvalidDataException>();
        File.WriteAllText(System.IO.Path.Combine(source, "Fixture.nuspec"),
            """<package><metadata><id>Wrong.Package</id><version>1.0.0</version></metadata></package>""");
        var wrong = workspace.Path("wrong.nupkg");
        ZipFile.CreateFromDirectory(source, wrong);
        Func<Task> mismatch = async () => { using var _ = await store.PrepareAsync(
            new("wrong.nupkg", LocalPath: wrong, PackageId: "Selected.Package", Version: "1.0.0"), null, default); };
        await mismatch.Should().ThrowAsync<InvalidDataException>();
        store.ReadInstalled().Should().BeEmpty();
        Directory.GetDirectories(workspace.Path("store"), "inspection-*").Should().BeEmpty();
    }

    [Test]
    public void EnablementPreservesProviderAndUnrelatedSecretConfiguration()
    {
        using var workspace = new Workspace();
        var root = workspace.Path("external");
        Directory.CreateDirectory(root);
        var original = new[] { new SupprocomSecretSetting("Provider:Key", "fixture"), new SupprocomSecretSetting("Secret:Other", "retain") };
        var result = BundledModuleSetup.UpdateSettings(original, root, [new("fixture", "Fixture", "1.0.0")], false)
            .ToDictionary(item => item.Key, item => item.Value);
        result["Secret:Other"].Should().Be("retain");
        result["Provider:Key"].Should().Be("fixture");
        result["Packages:fixture"].Should().Be("false");
        result["ExternalRegistrations:frontend-modules:Path"].Should().Be(root);
    }

    [Test]
    public async Task ReleaseAssetsAreExplicitChoicesAndRedirectsCannotCarryCredentialsOrEscapeSources()
    {
        var requests = new List<(string Host, string? Authorization)>();
        using var sources = new ModulePackageSources(new HttpClient(new Handler(request =>
        {
            requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            return request.RequestUri.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"assets":[{"name":"module.zip","browser_download_url":"https://github.com/owner/repo/releases/download/v1/module.zip"},
                    {"name":"module.nupkg","browser_download_url":"https://github.com/owner/repo/releases/download/v1/module.nupkg"}]}
                    """) }
                : new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.invalid/module.zip") } };
        })));
        var choices = await sources.ResolveAsync("https://github.com/owner/repo/releases/tag/v1", "not-forwarded", default);
        choices.Select(choice => choice.Name).Should().Equal("module.zip", "module.nupkg");
        using var output = new MemoryStream();
        Func<Task> download = () => sources.DownloadAsync(choices[0], output, "not-forwarded", default);
        await download.Should().ThrowAsync<InvalidDataException>();
        requests.Should().OnlyContain(request => request.Authorization == null);
        requests.Should().NotContain(request => request.Host == "attacker.invalid");
    }

    [TestCase("http://github.com/owner/repo/releases/latest")]
    [TestCase("https://user:password@github.com/owner/repo/releases/latest")]
    [TestCase("https://github.com:8443/owner/repo/releases/latest")]
    [TestCase("https://attacker.invalid/module.nupkg")]
    [TestCase("https://127.0.0.1/module.nupkg")]
    public void InvalidDownloadAuthoritiesAreRejected(string url) =>
        Assert.Throws<InvalidDataException>(() => ModulePackageSources.RequireDownloadUri(new Uri(url)));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpclaw-frontend-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public ModulePackageStore Store() => new(Path("store"), new ModulePackageSources(new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected network")))));
        public string CreatePayload()
        {
            var source = Path("source");
            Directory.CreateDirectory(source);
            var assembly = typeof(ModulePackageStoreTests).Assembly.Location;
            File.Copy(assembly, System.IO.Path.Combine(source, "Fixture.dll"));
            File.WriteAllText(System.IO.Path.Combine(source, "package.json"), JsonSerializer.Serialize(new
            {
                id = "frontend_fixture", displayName = "Frontend fixture", version = "0.5.0", toolPrefix = "ff",
                entryAssembly = "Fixture.dll", minHostVersion = "0.1.0", runtime = "dotnet", hostMode = "in-process",
            }));
            File.WriteAllText(System.IO.Path.Combine(source, "LICENSE.md"), "fixture legal notice");
            return source;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
