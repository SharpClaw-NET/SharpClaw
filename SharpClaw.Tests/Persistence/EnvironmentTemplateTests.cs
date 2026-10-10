using System.Text;
using Microsoft.Extensions.Configuration;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class EnvironmentTemplateTests
{
    [Test]
    public async Task MissingActiveEnvironment_IsCreatedFromDotenvTemplateAndProtectedAsync()
    {
        using var workspace = TempWorkspace.Create();
        var template = "Admin__Username=TemplateAdmin\n";
        workspace.Write(".env.template", template);
        workspace.Write(".dev.env.template", "Admin__Username=DevelopmentAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("TemplateAdmin");
        (await File.ReadAllTextAsync(workspace.Path(".env.template"), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Be(template);
        (await GetStateAsync(workspace).ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
        workspace.Files(".unreadable-*").Should().BeEmpty();
    }

    [Test]
    public async Task PlaintextActiveEnvironment_IsProtectedAfterSuccessfulLoadAsync()
    {
        using var workspace = TempWorkspace.Create();
        var template = "Admin__Username=TemplateAdmin\n";
        workspace.Write(".env.template", template);
        workspace.Write(".env", "Admin__Username=ActiveAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        (await File.ReadAllTextAsync(workspace.Path(".env.template"), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Be(template);
        (await GetStateAsync(workspace).ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("Admin__Username=ActiveAdmin");
    }

    [Test]
    public async Task WrongKeyProtectedActiveEnvironment_IsQuarantinedAndRestoredAsync()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=RecoveredAdmin\n");

        var foreignKeyPath = Path.Combine(workspace.Root, "foreign.key");
        var foreignStore = CreateStore(workspace, foreignKeyPath);
        await foreignStore.ReplaceDocumentAsync("Admin__Username=ForeignSecretAdmin\n").ConfigureAwait(false);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("RecoveredAdmin");
        workspace.Files(".unreadable-*").Should().ContainSingle();
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("RecoveredAdmin");
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().NotContain("ForeignSecretAdmin");
    }

    [Test]
    public async Task InvalidPlaintextActiveEnvironment_IsQuarantinedBeforeConfigurationBuildAsync()
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
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("RecoveredAdmin");
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
    public async Task NonEmptyReadableActiveEnvironment_IsNotOverwrittenByTemplateAsync()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        workspace.Write(".env", "Admin__Username=ActiveAdmin\n");

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        (await File.ReadAllTextAsync(workspace.Path(".env.template"), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().Be("Admin__Username=TemplateAdmin\n");
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("ActiveAdmin");
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
    public async Task PlaintextJsonWithComments_IsImportedOnceToProtectedDotenvAsync()
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
        (await GetStateAsync(workspace).ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("Admin__Username=\"ImportedAdmin\"");
    }

    [Test]
    public async Task EncryptedJsonWithComments_IsImportedOnceWithTheExistingInstallationKeyAsync()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var key = ApiKeyEncryptor.GenerateKey();
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.KeyPath)!);
        await File.WriteAllTextAsync(workspace.KeyPath, Convert.ToBase64String(key), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        var legacyJson = Encoding.UTF8.GetBytes(
            "{\n" +
            "  // existing encrypted installation JSONC\n" +
            "  \"Admin\": { \"Username\": \"EncryptedImportedAdmin\" },\n" +
            "}\n");
        await File.WriteAllBytesAsync(
            workspace.Path(".env"),
            ApiKeyEncryptor.EncryptBytes(legacyJson, key), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("EncryptedImportedAdmin");
        workspace.Files(".pre-supprocom-import-*").Should().ContainSingle();
        workspace.Files(".unreadable-*").Should().BeEmpty();
        (await File.ReadAllBytesAsync(workspace.KeyPath, TestContext.CurrentContext.CancellationToken).ConfigureAwait(false)).Should().HaveCount(32);
        (await GetStateAsync(workspace).ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
        (await ReadDocumentAsync(workspace).ConfigureAwait(false)).Should().Contain("Admin__Username=\"EncryptedImportedAdmin\"");
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
    public async Task DocumentStore_ReadReplaceAndRestart_UsesCompletePlaintextDocumentAsync()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Api__Url=http://127.0.0.1:48923\n");

        var store = CreateStore(workspace);
        await store.ReplaceDocumentAsync(
            "Api__Url=http://127.0.0.1:48924\nFeature__Enabled=true\n").ConfigureAwait(false);

        (await store.ReadDocumentAsync().ConfigureAwait(false)).Should().Contain("Api__Url=http://127.0.0.1:48924");
        (await store.GetStateAsync().ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);

        var restarted = CreateStore(workspace);
        (await restarted.ReadDocumentAsync().ConfigureAwait(false)).Should().Contain("Feature__Enabled=true");
        (await restarted.GetStateAsync().ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
    }

    [Test]
    public async Task ProtectionManager_UnprotectsThenNextLoadReprotectsAsync()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", "Admin__Username=TemplateAdmin\n");
        var store = CreateStore(workspace);
        await store.ReplaceDocumentAsync("Admin__Username=ProtectedAdmin\n").ConfigureAwait(false);
        SupprocomSecretFileStore manager = store;

        (await manager.GetStateAsync().ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
        await manager.UnprotectAsync().ConfigureAwait(false);
        (await manager.GetStateAsync().ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Plaintext);

        var restarted = CreateStore(workspace);
        (await restarted.ReadDocumentAsync().ConfigureAwait(false)).Should().Contain("ProtectedAdmin");
        (await restarted.GetStateAsync().ConfigureAwait(false)).Should().Be(SecretFileProtectionState.Protected);
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
        await CreateStore(workspace).GetStateAsync().ConfigureAwait(false);

    private static async Task<string> ReadDocumentAsync(TempWorkspace workspace) =>
        await CreateStore(workspace).ReadDocumentAsync().ConfigureAwait(false);

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
            catch (IOException exception)
            {
                TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                TestContext.Progress.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
            }
        }
    }
}
