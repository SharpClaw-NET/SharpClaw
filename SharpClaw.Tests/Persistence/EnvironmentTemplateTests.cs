using System.Text;
using Microsoft.Extensions.Configuration;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class EnvironmentTemplateTests
{
    [Test]
    public async Task MissingActiveEnvironment_IsCreatedFromDotenvTemplateAndProtected()
    {
        using var workspace = TempWorkspace.Create();
        var template = "Admin__Username=TemplateAdmin\n";
        workspace.Write(".env.template", template);
        workspace.Write(".dev.env.template", "Admin__Username=DevelopmentAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("TemplateAdmin");
        File.ReadAllText(workspace.Path(".env.template")).Should().Be(template);
        (await GetStateAsync(workspace)).Should().Be(SecretFileProtectionState.Protected);
        workspace.Files(".unreadable-*").Should().BeEmpty();
    }

    [Test]
    public async Task PlaintextActiveEnvironment_IsProtectedAfterSuccessfulLoad()
    {
        using var workspace = TempWorkspace.Create();
        var template = "Admin__Username=TemplateAdmin\n";
        workspace.Write(".env.template", template);
        workspace.Write(".env", "Admin__Username=ActiveAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        File.ReadAllText(workspace.Path(".env.template")).Should().Be(template);
        (await GetStateAsync(workspace)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace)).Should().Contain("Admin__Username=ActiveAdmin");
    }

    [Test]
    public async Task WrongKeyProtectedActiveEnvironment_IsQuarantinedAndRestored()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=RecoveredAdmin\n");

        var foreignKeyPath = Path.Combine(workspace.Root, "foreign.key");
        var foreignStore = CreateStore(workspace, foreignKeyPath);
        await foreignStore.ReplaceDocumentAsync("Admin__Username=ForeignSecretAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("RecoveredAdmin");
        workspace.Files(".unreadable-*").Should().ContainSingle();
        (await ReadDocumentAsync(workspace)).Should().Contain("RecoveredAdmin");
        (await ReadDocumentAsync(workspace)).Should().NotContain("ForeignSecretAdmin");
    }

    [Test]
    public async Task InvalidPlaintextActiveEnvironment_IsQuarantinedBeforeConfigurationBuild()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=RecoveredAdmin\n");
        workspace.Write(".env", "{ invalid json");

        var builder = new ConfigurationBuilder();
        builder.AddLocalEnvironmentFrom(
            workspace.EnvironmentDirectory,
            isDevelopment: false,
            workspace.Paths);
        var configuration = builder.Build();

        configuration["Admin:Username"].Should().Be("RecoveredAdmin");
        workspace.Files(".unreadable-*").Should().ContainSingle();
        (await ReadDocumentAsync(workspace)).Should().Contain("RecoveredAdmin");
    }

    [Test]
    public void InvalidPlaintextDevelopmentEnvironment_IsQuarantinedAndOverlaysTemplate()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=BaseAdmin\n");
        workspace.Write(".dev.env.template", "Admin__Username=DevelopmentAdmin\n");
        workspace.Write(".dev.env", "{ invalid dev json");

        var configuration = BuildLocal(workspace, isDevelopment: true);

        configuration["Admin:Username"].Should().Be("DevelopmentAdmin");
        workspace.Files(".unreadable-*").Should().ContainSingle();
    }

    [Test]
    public async Task NonEmptyReadableActiveEnvironment_IsNotOverwrittenByTemplate()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        workspace.Write(".env", "Admin__Username=ActiveAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        File.ReadAllText(workspace.Path(".env.template")).Should().Be("Admin__Username=TemplateAdmin\n");
        (await ReadDocumentAsync(workspace)).Should().Contain("ActiveAdmin");
    }

    [Test]
    public void CommentedCanonicalDotenv_LoadsWithoutQuarantine()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        workspace.Write(
            ".env",
            "# comments are valid in canonical SharpClaw dotenv files\n" +
            "Admin__Username=CommentedActiveAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("CommentedActiveAdmin");
        workspace.Files(".unreadable-*").Should().BeEmpty();
    }

    [Test]
    public async Task PlaintextJsonWithComments_IsImportedOnceToProtectedDotenv()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        workspace.Write(
            ".env",
            "{\n" +
            "  // existing installation JSONC\n" +
            "  \"Admin\": { \"Username\": \"ImportedAdmin\" },\n" +
            "}\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ImportedAdmin");
        workspace.Files(".pre-supprocom-import-*").Should().ContainSingle();
        (await GetStateAsync(workspace)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace)).Should().Contain("Admin__Username=\"ImportedAdmin\"");
    }

    [Test]
    public async Task EncryptedJsonWithComments_IsImportedOnceWithTheExistingInstallationKey()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var key = ApiKeyEncryptor.GenerateKey();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.KeyPath)!);
        File.WriteAllText(workspace.KeyPath, Convert.ToBase64String(key));
        var legacyJson = Encoding.UTF8.GetBytes(
            "{\n" +
            "  // existing encrypted installation JSONC\n" +
            "  \"Admin\": { \"Username\": \"EncryptedImportedAdmin\" },\n" +
            "}\n");
        File.WriteAllBytes(
            workspace.Path(".env"),
            ApiKeyEncryptor.EncryptBytes(legacyJson, key));

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("EncryptedImportedAdmin");
        workspace.Files(".pre-supprocom-import-*").Should().ContainSingle();
        workspace.Files(".unreadable-*").Should().BeEmpty();
        File.ReadAllBytes(workspace.KeyPath).Should().HaveCount(32);
        (await GetStateAsync(workspace)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace)).Should().Contain("Admin__Username=\"EncryptedImportedAdmin\"");
    }

    [Test]
    public void EncryptedTemplate_IsRejectedAsInvalidPortableTemplate()
    {
        using var workspace = TempWorkspace.Create();
        var envelope = new byte[1 + 12 + 16];
        envelope[0] = 0x01;
        File.WriteAllBytes(workspace.Path(".env.template"), envelope);

        var act = () => BuildLocal(workspace, isDevelopment: false);

        var exception = act.Should().Throw<SupprocomSecretsException>().Which;
        exception.Code.Should().Be("EncryptedTemplate");
    }

    [Test]
    public void GatewayMissingActiveEnvironment_UsesCanonicalTemplate()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "InternalApi__BaseUrl=http://127.0.0.1:48923\n");
        workspace.Write(".dev.env.template", "InternalApi__BaseUrl=http://127.0.0.1:48925\n");

        var configuration = BuildGateway(workspace, isDevelopment: false);

        var options = GatewayEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            isDevelopment: false,
            workspace.KeyPath);
        options.File.InstallationKeyPath.Should().Be(workspace.KeyPath);

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48923");
        File.Exists(workspace.Path(".env")).Should().BeTrue();
        File.ReadAllText(workspace.Path(".env.template")).Should()
            .Be("InternalApi__BaseUrl=http://127.0.0.1:48923\n");
    }

    [Test]
    public void GatewayInvalidActiveEnvironment_IsQuarantined()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "InternalApi__BaseUrl=http://127.0.0.1:48924\n");
        workspace.Write(".env", "{ invalid json");

        var configuration = BuildGateway(workspace, isDevelopment: false);

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48924");
        workspace.Files(".unreadable-*").Should().ContainSingle();
    }

    [Test]
    public void GatewayDevelopmentOverlay_UsesDevelopmentTemplateAndActiveFile()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "InternalApi__BaseUrl=http://127.0.0.1:48923\n");
        workspace.Write(".dev.env.template", "InternalApi__BaseUrl=http://127.0.0.1:48925\n");
        workspace.Write(".dev.env", "# development override\nInternalApi__BaseUrl=http://127.0.0.1:48926\n");

        var configuration = BuildGateway(workspace, isDevelopment: true);

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48926");
        workspace.Files(".unreadable-*").Should().BeEmpty();
    }

    [Test]
    public void LoaderOptions_UseBothActiveAndTemplatePairsInAssemblyEnvironmentDirectory()
    {
        using var workspace = TempWorkspace.Create();

        var production = LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            isDevelopment: false,
            workspace.Paths);
        var development = LocalEnvironment.CreateSecretsOptions(
            workspace.EnvironmentDirectory,
            isDevelopment: true,
            workspace.Paths);

        production.EnvironmentName.Should().Be("Production");
        production.File.ActiveName.Should().Be(".env");
        production.File.TemplateName.Should().Be(".env.template");
        development.EnvironmentName.Should().Be("Development");
        development.File.DevelopmentName.Should().Be(".dev.env");
        development.File.DevelopmentTemplateName.Should().Be(".dev.env.template");
        development.File.DevelopmentComposition.Should().Be(SecretFileComposition.Overlay);
        LocalEnvironment.ResolveActiveEnvFilePath().Should()
            .Contain($"{Path.DirectorySeparatorChar}Environment{Path.DirectorySeparatorChar}.env");
        LocalEnvironment.ResolveActiveEnvFilePath().Should()
            .NotContain($"{Path.DirectorySeparatorChar}config{Path.DirectorySeparatorChar}.env");
    }

    [Test]
    public async Task DocumentStore_ReadReplaceAndRestart_UsesCompletePlaintextDocument()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Api__Url=http://127.0.0.1:48923\n");

        var store = CreateStore(workspace);
        await store.ReplaceDocumentAsync(
            "Api__Url=http://127.0.0.1:48924\nFeature__Enabled=true\n");

        (await store.ReadDocumentAsync()).Should().Contain("Api__Url=http://127.0.0.1:48924");
        (await store.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);

        var restarted = CreateStore(workspace);
        (await restarted.ReadDocumentAsync()).Should().Contain("Feature__Enabled=true");
        (await restarted.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
    }

    [Test]
    public async Task ProtectionManager_UnprotectsThenNextLoadReprotects()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var store = CreateStore(workspace);
        await store.ReplaceDocumentAsync("Admin__Username=ProtectedAdmin\n");
        ISecretFileProtectionManager manager = store;

        (await manager.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
        await manager.UnprotectAsync();
        (await manager.GetStateAsync()).Should().Be(SecretFileProtectionState.Plaintext);

        var restarted = CreateStore(workspace);
        (await restarted.ReadDocumentAsync()).Should().Contain("ProtectedAdmin");
        (await restarted.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
    }

    private static IConfiguration BuildLocal(TempWorkspace workspace, bool isDevelopment) =>
        new ConfigurationBuilder()
            .AddLocalEnvironmentFrom(
                workspace.EnvironmentDirectory,
                isDevelopment,
                workspace.Paths)
            .Build();

    private static IConfiguration BuildGateway(TempWorkspace workspace, bool isDevelopment) =>
        new ConfigurationBuilder()
            .AddGatewayEnvironmentFrom(
                workspace.EnvironmentDirectory,
                isDevelopment,
                workspace.KeyPath)
            .Build();

    private static SupprocomSecretFileStore CreateStore(
        TempWorkspace workspace,
        string? installationKeyPath = null) =>
        new(CreateOptions(workspace, installationKeyPath));

    private static SupprocomSecretsOptions CreateOptions(
        TempWorkspace workspace,
        string? installationKeyPath = null) =>
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
                InstallationKeyPath = installationKeyPath ?? workspace.KeyPath
            }
        };

    private static async Task<SecretFileProtectionState> GetStateAsync(TempWorkspace workspace) =>
        await CreateStore(workspace).GetStateAsync();

    private static async Task<string> ReadDocumentAsync(TempWorkspace workspace) =>
        await CreateStore(workspace).ReadDocumentAsync();

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string root)
        {
            Root = root;
            EnvironmentDirectory = System.IO.Path.Combine(root, "Environment");
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                System.IO.Path.Combine(root, "instance"));
            KeyPath = Paths.GetSecretFilePath("encryption-key");
            Directory.CreateDirectory(EnvironmentDirectory);
        }

        public string Root { get; }
        public string EnvironmentDirectory { get; }
        public SharpClawInstancePaths Paths { get; }
        public string KeyPath { get; }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SharpClaw.Tests",
                Guid.NewGuid().ToString("N"));
            return new TempWorkspace(root);
        }

        public string Path(string fileName) => System.IO.Path.Combine(EnvironmentDirectory, fileName);

        public string[] Files(string pattern) => Directory.GetFiles(EnvironmentDirectory, pattern);

        public void Write(string fileName, string content) => File.WriteAllText(Path(fileName), content);

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
