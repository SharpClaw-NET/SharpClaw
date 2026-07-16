using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.Host.Cli;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Cli;

[NonParallelizable]
public sealed class EnvironmentCliCommandTests
{
    [Test]
    public async Task StatusAndUnlockUseThePackageProtectionManagerAndStartupRelocksTheFile()
    {
        using var workspace = TestWorkspace.Create();
        var options = CreateOptions(workspace);
        var store = new SupprocomSecretFileStore(options);
        await store.ReplaceDocumentAsync("Api__Url=http://127.0.0.1:48924\n");
        using var services = CreateServices(store);

        var protectedStatus = await CaptureOutputAsync(() =>
            CliDispatcher.TryHandleAsync(["env", "status"], services));
        protectedStatus.Should().Contain("encrypted");

        var unlockOutput = await CaptureOutputAsync(() =>
            CliDispatcher.TryHandleAsync(["env", "unlock"], services));
        unlockOutput.Should().Contain("decrypted");
        (await store.GetStateAsync()).Should().Be(SecretFileProtectionState.Plaintext);

        var plaintextStatus = await CaptureOutputAsync(() =>
            CliDispatcher.TryHandleAsync(["env", "status"], services));
        plaintextStatus.Should().Contain("plaintext");

        _ = new ConfigurationBuilder().AddSupprocomSecrets(options).Build();
        var restartedStore = new SupprocomSecretFileStore(options);
        (await restartedStore.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
    }

    private static ServiceProvider CreateServices(SupprocomSecretFileStore store)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SharpClawDbContext>(dbOptions =>
            dbOptions.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvEditor:AllowNonAdmin"] = "true"
            })
            .Build());
        services.AddScoped<SessionService>();
        services.AddSingleton<ISecretDocumentStore>(store);
        services.AddSingleton<ISecretFileProtectionManager>(store);
        services.AddScoped(_ => new EnvFileService(
            _.GetRequiredService<SharpClawDbContext>(),
            _.GetRequiredService<SessionService>(),
            _.GetRequiredService<IConfiguration>(),
            store));
        return services.BuildServiceProvider();
    }

    private static SupprocomSecretsOptions CreateOptions(TestWorkspace workspace) =>
        new()
        {
            EnvironmentName = "Production",
            FileOverridesProcessEnvironment = true,
            File =
            {
                Directory = workspace.EnvironmentDirectory,
                ActiveName = ".env",
                DevelopmentName = ".dev.env",
                TemplateName = ".env.template",
                DevelopmentTemplateName = ".dev.env.template",
                Import = SecretFileImport.JsonWithCommentsOnce,
                DevelopmentComposition = SecretFileComposition.Overlay,
                Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyPath = workspace.KeyPath
            }
        };

    private static async Task<string> CaptureOutputAsync(Func<Task<bool>> action)
    {
        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root)
        {
            Root = root;
            EnvironmentDirectory = Path.Combine(root, "Environment");
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                Path.Combine(root, "instance"));
            KeyPath = Paths.GetSecretFilePath("encryption-key");
            Directory.CreateDirectory(EnvironmentDirectory);
        }

        public string Root { get; }
        public string EnvironmentDirectory { get; }
        public SharpClawInstancePaths Paths { get; }
        public string KeyPath { get; }

        public static TestWorkspace Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SharpClaw.Tests",
                Guid.NewGuid().ToString("N"));
            return new TestWorkspace(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
