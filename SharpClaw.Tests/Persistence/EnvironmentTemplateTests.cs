using Microsoft.Extensions.Configuration;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Infrastructure.Configuration;
using SharpClaw.Utils.Instances;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class EnvironmentTemplateTests
{
    [Test]
    public void MissingActiveEnvironment_IsCreatedFromTemplateInEnvironmentDirectory()
    {
        using var workspace = TempWorkspace.Create();
        var templateJson = TemplateJson("TemplateAdmin");
        workspace.Write(".env.template", templateJson);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("TemplateAdmin");
        File.Exists(workspace.Path(".env")).Should().BeTrue();
        File.ReadAllText(workspace.Path(".env.template")).Should().Be(templateJson);
        EncryptedEnvFile.IsEncryptedOnDisk(workspace.Path(".env.template")).Should().BeFalse();
        LocalEnvironment.ResolveActiveEnvFilePath().Should().Contain($"{Path.DirectorySeparatorChar}Environment{Path.DirectorySeparatorChar}.env");
        LocalEnvironment.ResolveActiveEnvFilePath().Should().NotContain($"{Path.DirectorySeparatorChar}config{Path.DirectorySeparatorChar}.env");
    }

    [Test]
    public async Task PlaintextActiveEnvironment_IsEncryptedAfterSuccessfulLoad()
    {
        using var workspace = TempWorkspace.Create();
        var templateJson = TemplateJson("TemplateAdmin");
        workspace.Write(".env.template", templateJson);
        workspace.Write(".env", TemplateJson("ActiveAdmin"));

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        File.ReadAllText(workspace.Path(".env.template")).Should().Be(templateJson);
        EncryptedEnvFile.IsEncryptedOnDisk(workspace.Path(".env.template")).Should().BeFalse();
        EncryptedEnvFile.IsEncryptedOnDisk(workspace.Path(".env")).Should().BeTrue();

        var decrypted = await ReadActiveAsync(workspace, ".env");
        decrypted.Should().Contain("ActiveAdmin");
    }

    [Test]
    public async Task WrongKeyEncryptedActiveEnvironment_IsQuarantinedAndRecreatedFromTemplate()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", TemplateJson("RecoveredAdmin"));
        await EncryptedEnvFile.WriteAsync(
            workspace.Path(".env"),
            TemplateJson("ForeignSecretAdmin"),
            ApiKeyEncryptor.GenerateKey(),
            encrypt: true);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("RecoveredAdmin");
        workspace.Files(".env.unreadable-*").Should().ContainSingle();
        EncryptedEnvFile.IsEncryptedOnDisk(workspace.Path(".env")).Should().BeTrue();

        var recreated = await ReadActiveAsync(workspace, ".env");
        recreated.Should().Contain("RecoveredAdmin");
        recreated.Should().NotContain("ForeignSecretAdmin");
    }

    [Test]
    public async Task InvalidPlaintextActiveEnvironment_IsQuarantinedBeforeConfigurationBuild()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", TemplateJson("RecoveredAdmin"));
        workspace.Write(".env", "{ invalid json");

        var builder = new ConfigurationBuilder();
        builder.AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment: false, workspace.Paths);
        var configuration = builder.Build();

        configuration["Admin:Username"].Should().Be("RecoveredAdmin");
        workspace.Files(".env.unreadable-*").Should().ContainSingle();
        var recreated = await ReadActiveAsync(workspace, ".env");
        recreated.Should().Contain("RecoveredAdmin");
        recreated.Should().NotContain("invalid json");
    }

    [Test]
    public async Task InvalidPlaintextDevEnvironment_IsQuarantinedBeforeConfigurationBuild()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", TemplateJson("BaseAdmin"));
        workspace.Write(".dev.env.template", TemplateJson("DevRecoveredAdmin"));
        workspace.Write(".dev.env", "{ invalid dev json");

        var builder = new ConfigurationBuilder();
        builder.AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment: true, workspace.Paths);
        var configuration = builder.Build();

        configuration["Admin:Username"].Should().Be("DevRecoveredAdmin");
        workspace.Files(".dev.env.unreadable-*").Should().ContainSingle();
        var recreated = await ReadActiveAsync(workspace, ".dev.env");
        recreated.Should().Contain("DevRecoveredAdmin");
        recreated.Should().NotContain("invalid dev json");
    }

    [Test]
    public void NonEmptyReadableActiveEnvironment_IsNotOverwrittenByTemplate()
    {
        using var workspace = TempWorkspace.Create();
        var activeJson = TemplateJson("ActiveAdmin");
        var templateJson = TemplateJson("TemplateAdmin");
        workspace.Write(".env.template", templateJson);
        workspace.Write(".env", activeJson);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("ActiveAdmin");
        File.ReadAllText(workspace.Path(".env.template")).Should().Be(templateJson);
        ReadActivePossiblyEncrypted(workspace, ".env").Should().Contain("ActiveAdmin");
    }

    [Test]
    public void CommentedActiveEnvironment_LoadsWithoutQuarantine()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", TemplateJson("TemplateAdmin"));
        workspace.Write(
            ".env",
            """
            {
              // comments are valid in SharpClaw env files
              "Admin": {
                "Username": "CommentedActiveAdmin",
                "Password": "123456",
              },
            }
            """);

        var configuration = BuildLocal(workspace, isDevelopment: false);

        configuration["Admin:Username"].Should().Be("CommentedActiveAdmin");
        workspace.Files(".env.unreadable-*").Should().BeEmpty();
    }

    [Test]
    public async Task EncryptedTemplate_IsRejectedAsInvalidPortableTemplate()
    {
        using var workspace = TempWorkspace.Create();
        await EncryptedEnvFile.WriteAsync(
            workspace.Path(".env.template"),
            TemplateJson("EncryptedTemplate"),
            ApiKeyEncryptor.GenerateKey(),
            encrypt: true);

        var act = () => BuildLocal(workspace, isDevelopment: false);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*template*encrypted*plaintext*portable*");
    }

    [Test]
    public void GatewayMissingActiveEnvironment_IsCreatedFromTemplateInEnvironmentDirectory()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", GatewayJson("http://127.0.0.1:48923"));

        var configuration = BuildGateway(workspace, isDevelopment: false);

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48923");
        File.Exists(workspace.Path(".env")).Should().BeTrue();
        File.ReadAllText(workspace.Path(".env.template")).Should().Contain("48923");
    }

    [Test]
    public void GatewayInvalidPlaintextActiveEnvironment_IsQuarantinedBeforeConfigurationBuild()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", GatewayJson("http://127.0.0.1:48924"));
        workspace.Write(".env", "{ invalid json");

        var builder = new ConfigurationBuilder();
        builder.AddGatewayEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment: false);
        var configuration = builder.Build();

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48924");
        workspace.Files(".env.unreadable-*").Should().ContainSingle();
        File.ReadAllText(workspace.Path(".env.template")).Should().Contain("48924");
    }

    [Test]
    public void GatewayCommentedActiveEnvironment_LoadsWithoutQuarantine()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", GatewayJson("http://127.0.0.1:48923"));
        workspace.Write(
            ".env",
            """
            {
              // comments are valid in SharpClaw gateway env files
              "InternalApi": {
                "BaseUrl": "http://127.0.0.1:48926",
                "TimeoutSeconds": "300",
              },
            }
            """);

        var configuration = BuildGateway(workspace, isDevelopment: false);

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48926");
        workspace.Files(".env.unreadable-*").Should().BeEmpty();
    }

    [Test]
    public void GatewayInvalidPlaintextDevEnvironment_IsQuarantinedBeforeConfigurationBuild()
    {
        using var workspace = TempWorkspace.Create();
        workspace.Write(".env.template", GatewayJson("http://127.0.0.1:48923"));
        workspace.Write(".dev.env.template", GatewayJson("http://127.0.0.1:48925"));
        workspace.Write(".dev.env", "{ invalid dev json");

        var builder = new ConfigurationBuilder();
        builder.AddGatewayEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment: true);
        var configuration = builder.Build();

        configuration["InternalApi:BaseUrl"].Should().Be("http://127.0.0.1:48925");
        workspace.Files(".dev.env.unreadable-*").Should().ContainSingle();
        File.ReadAllText(workspace.Path(".dev.env.template")).Should().Contain("48925");
    }

    private static IConfiguration BuildLocal(TempWorkspace workspace, bool isDevelopment) =>
        new ConfigurationBuilder()
            .AddLocalEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment, workspace.Paths)
            .Build();

    private static IConfiguration BuildGateway(TempWorkspace workspace, bool isDevelopment) =>
        new ConfigurationBuilder()
            .AddGatewayEnvironmentFrom(workspace.EnvironmentDirectory, isDevelopment)
            .Build();

    private static async Task<string> ReadActiveAsync(TempWorkspace workspace, string fileName)
    {
        var key = Convert.FromBase64String(
            File.ReadAllText(workspace.Paths.GetSecretFilePath("encryption-key")).Trim());
        return await EncryptedEnvFile.ReadAsync(workspace.Path(fileName), key);
    }

    private static string ReadActivePossiblyEncrypted(TempWorkspace workspace, string fileName)
    {
        if (!EncryptedEnvFile.IsEncryptedOnDisk(workspace.Path(fileName)))
            return File.ReadAllText(workspace.Path(fileName));

        return ReadActiveAsync(workspace, fileName).GetAwaiter().GetResult();
    }

    private static string TemplateJson(string adminUser) =>
        $$"""
        {
          "Admin": {
            "Username": "{{adminUser}}",
            "Password": "123456"
          },
          "Auth": {
            "DisableApiKeyCheck": "false",
            "DisableAccessTokenCheck": "false"
          }
        }
        """;

    private static string GatewayJson(string baseUrl) =>
        $$"""
        {
          "InternalApi": {
            "BaseUrl": "{{baseUrl}}",
            "TimeoutSeconds": "300"
          }
        }
        """;

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string root)
        {
            Root = root;
            EnvironmentDirectory = System.IO.Path.Combine(root, "Environment");
            Paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                System.IO.Path.Combine(root, "instance"));
            Directory.CreateDirectory(EnvironmentDirectory);
        }

        public string Root { get; }
        public string EnvironmentDirectory { get; }
        public SharpClawInstancePaths Paths { get; }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SharpClaw.Tests", Guid.NewGuid().ToString("N"));
            return new TempWorkspace(root);
        }

        public string Path(string fileName) => System.IO.Path.Combine(EnvironmentDirectory, fileName);

        public string[] Files(string pattern) => Directory.GetFiles(EnvironmentDirectory, pattern);

        public void Write(string fileName, string content) =>
            File.WriteAllText(Path(fileName), content);

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
