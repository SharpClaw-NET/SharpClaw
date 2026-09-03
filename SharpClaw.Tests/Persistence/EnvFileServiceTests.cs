using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Persistence;

public sealed class EnvFileServiceTests
{
    [Test]
    public async Task ReadAndWriteUseThePackageDocumentStoreForTheCompleteProtectedDocument()
    {
        using var workspace = TestWorkspace.Create();
        var options = CreateOptions(workspace);
        var store = new SupprocomSecretFileStore(options);
        var databaseName = Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using var db = new SharpClawDbContext(dbOptions);
        var session = new SessionService { UserId = Guid.NewGuid() };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvEditor:AllowNonAdmin"] = "true"
            })
            .Build();
        var service = new EnvFileService(db, session, configuration, store);
        const string document = "Api__Url=http://127.0.0.1:48924\nProvider__Token=secret-value\n";

        await service.WriteAsync(document);

        (await service.ReadAsync()).Should().Be(document);
        (await store.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);

        var restartedStore = new SupprocomSecretFileStore(options);
        (await restartedStore.ReadDocumentAsync()).Should().Be(document);
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
