using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Tests.TestHarness;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Modules;

[TestFixture]
[NonParallelizable]
public sealed class ExternalModuleEnvironmentMutationTests
{
    [Test]
    public async Task ExternalModulePath_UsesLowestUnusedIndexAndPreservesPartialEntries()
    {
        await using var host = ChatHarnessHost.Create();
        var store = host.RootServices.GetRequiredService<ISecretDocumentStore>();
        await store.ReplaceDocumentAsync(
            "Unrelated=keep\n"
            + "ExternalModules__0=scalar-partial\n"
            + "ExternalModules__2__Unknown=reserved\n"
            + "ExternalModules__4__Path__Nested=not-the-path-field\n"
            + "ExternalModules__6__Path=\n");

        string newPath = Path.Combine(host.InstanceRoot, "new-module");
        await IgnoreMissingModuleAsync(host, newPath);

        var settings = ReadSettings(await store.ReadDocumentAsync());
        settings.Should().Contain(new SupprocomSecretSetting("Unrelated", "keep"));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:0", "scalar-partial"));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:1:Path", newPath));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:1:Enabled", "false"));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:2:Unknown", "reserved"));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:4:Path:Nested", "not-the-path-field"));
        settings.Should().Contain(new SupprocomSecretSetting(
            "ExternalModules:6:Path", ""));

        await AssertProtectedAsync(host);
        var restarted = new SupprocomSecretFileStore(
            LocalEnvironment.CreateSecretsOptions(
                Path.Combine(host.InstanceRoot, "Environment"),
                isDevelopment: false,
                host.RootServices.GetRequiredService<SharpClawInstancePaths>()));
        ReadSettings(await restarted.ReadDocumentAsync())
            .Should().Contain(new SupprocomSecretSetting(
                "ExternalModules:1:Enabled", "false"));
    }

    [Test]
    public async Task ExternalModulePath_DoesNotDuplicateCanonicalPath()
    {
        await using var host = ChatHarnessHost.Create();
        var store = host.RootServices.GetRequiredService<ISecretDocumentStore>();
        string modulePath = Path.Combine(host.InstanceRoot, "duplicate-module");
        string alternateSpelling = Path.Combine(
            host.InstanceRoot,
            ".",
            "DUPLICATE-MODULE")
            + Path.DirectorySeparatorChar;

        await store.ReplaceDocumentAsync(
            $"ExternalModules__0__Path={alternateSpelling.Replace('\\', '/')}\n"
            + "ExternalModules__0__Enabled=true\n");

        await IgnoreMissingModuleAsync(host, modulePath);

        var paths = ReadSettings(await store.ReadDocumentAsync())
            .Where(setting => setting.Key.EndsWith(":Path", StringComparison.OrdinalIgnoreCase))
            .ToList();
        paths.Should().ContainSingle();
        paths[0].Value.Should().Be(alternateSpelling.Replace('\\', '/'));
    }

    [Test]
    public async Task ExternalModulePath_ProviderPointerFailureLeavesPointerBytesUnchanged()
    {
        await using var host = ChatHarnessHost.Create();
        var store = host.RootServices.GetRequiredService<ISecretDocumentStore>();
        await store.ReplaceDocumentAsync("Unrelated=before\n");

        string activePath = Path.Combine(host.InstanceRoot, "Environment", ".env");
        File.WriteAllText(activePath, "SUPPROCOM_SECRET_SOURCE=wincred://Supprocom/Test\n");
        byte[] before = File.ReadAllBytes(activePath);

        var exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            () => ExpectMissingModuleAsync(host, Path.Combine(host.InstanceRoot, "blocked-module")));

        exception!.Code.Should().NotBeNullOrWhiteSpace();
        File.ReadAllBytes(activePath).Should().Equal(before);
    }

    [Test]
    public async Task ExternalModulePath_CancellationLeavesProtectedCiphertextUnchanged()
    {
        await using var host = ChatHarnessHost.Create();
        var store = host.RootServices.GetRequiredService<ISecretDocumentStore>();
        await store.ReplaceDocumentAsync("Unrelated=before\n");
        await AssertProtectedAsync(host);
        byte[] before = File.ReadAllBytes(
            Path.Combine(host.InstanceRoot, "Environment", ".env"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            () => ExpectMissingModuleAsync(
                host,
                Path.Combine(host.InstanceRoot, "cancelled-module"),
                cancellation.Token));

        File.ReadAllBytes(Path.Combine(host.InstanceRoot, "Environment", ".env"))
            .Should().Equal(before);
        await AssertProtectedAsync(host);
    }

    [Test]
    public async Task ExternalModulePath_ConcurrentLoadsPreserveBothEntries()
    {
        await using var host = ChatHarnessHost.Create();
        var store = host.RootServices.GetRequiredService<ISecretDocumentStore>();
        await store.ReplaceDocumentAsync("Unrelated=before\n");

        string first = Path.Combine(host.InstanceRoot, "concurrent-one");
        string second = Path.Combine(host.InstanceRoot, "concurrent-two");
        await Task.WhenAll(
            IgnoreMissingModuleAsync(host, first),
            IgnoreMissingModuleAsync(host, second));

        var settings = ReadSettings(await store.ReadDocumentAsync());
        settings
            .Where(setting => setting.Key.EndsWith(":Path", StringComparison.OrdinalIgnoreCase))
            .Select(setting => setting.Value)
            .Should()
            .BeEquivalentTo([first, second]);
        settings
            .Where(setting => setting.Key.EndsWith(":Enabled", StringComparison.OrdinalIgnoreCase))
            .Select(setting => setting.Value)
            .Should()
            .BeEquivalentTo(["false", "false"]);
        settings.Should().Contain(new SupprocomSecretSetting("Unrelated", "before"));
    }

    private static async Task ExpectMissingModuleAsync(
        ChatHarnessHost host,
        string path,
        CancellationToken cancellationToken = default)
    {
        await host.Services
            .GetRequiredService<ModuleService>()
            .LoadExternalFromAbsolutePathAsync(
                path,
                host.RootServices,
                cancellationToken);
    }

    private static async Task IgnoreMissingModuleAsync(ChatHarnessHost host, string path)
    {
        try
        {
            await ExpectMissingModuleAsync(host, path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static IReadOnlyList<SupprocomSecretSetting> ReadSettings(string document) =>
        SupprocomSecretDocument.Parse(document).Settings;

    private static async Task AssertProtectedAsync(ChatHarnessHost host)
    {
        var manager = host.RootServices.GetRequiredService<ISecretFileProtectionManager>();
        (await manager.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
    }
}
