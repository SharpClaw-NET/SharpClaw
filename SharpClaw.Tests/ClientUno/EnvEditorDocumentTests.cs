using System.Security.Cryptography;
using SharpClaw.Presentation;
using Supprocom.Secrets;

namespace SharpClaw.Tests.ClientUno;

[TestFixture]
public sealed class EnvEditorDocumentTests
{
    [Test]
    public void StructuredEditor_UsesCanonicalDotenvAndPackageKeyNormalization()
    {
        var settings = EnvEditorPage.ParseStructuredDocument(
            "# editor fixture\nProvider__ApiKey=abc\nNested__Value=one\n");

        settings.Should().HaveCount(2);
        settings.Should().Contain(setting => setting.Key == "Provider:ApiKey" && setting.Value == "abc");
        settings.Should().Contain(setting => setting.Key == "Nested:Value" && setting.Value == "one");

        var document = EnvEditorPage.SerializeStructuredDocument(settings);

        document.Should().Contain("Provider__ApiKey=\"abc\"");
        document.Should().Contain("Nested__Value=\"one\"");
        document.Should().NotContain("{");
        document.Should().NotContain("}");
    }

    [Test]
    public void StructuredEditor_RejectsReservedPackageControlKeysWithoutCopyingLocalParserRules()
    {
        var act = () => EnvEditorPage.ParseStructuredDocument(
            "SUPPROCOM_SECRET_SOURCE=wincred://Supprocom/Test\n");

        var exception = act.Should().Throw<SupprocomSecretsException>().Which;
        exception.Code.Should().Be("DetachedStructuredEditingUnsupported");
    }

    [Test]
    public async Task LocalEditorStore_UsesPackageTemplateRecoveryForMissingActiveFile()
    {
        var envDirectory = CreateTempDirectory();
        var keyPath = CreateInstallationKeyPath(envDirectory);
        try
        {
            var template = "Editor__Value=from-template\n";
            File.WriteAllText(Path.Combine(envDirectory, ".env.template"), template);

            var store = EnvEditorPage.CreateLocalSecretStore(envDirectory, keyPath);
            var document = await store.ReadDocumentAsync();

            document.Should().Contain("Editor__Value=from-template");
            File.ReadAllBytes(Path.Combine(envDirectory, ".env"))
                .Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(template));
            (await store.GetStateAsync()).Should().Be(SecretFileProtectionState.Protected);
            (await store.ReadDocumentAsync())
                .Should().Contain(template.TrimEnd());
            File.ReadAllText(Path.Combine(envDirectory, ".env.template")).Should().Be(template);
            AssertTempInstallationKey(keyPath);
        }
        finally
        {
            DeleteTempDirectory(envDirectory);
        }
    }

    [Test]
    public async Task LocalEditorStore_SavesCanonicalDocumentAndIsFailureAtomic()
    {
        var envDirectory = CreateTempDirectory();
        var keyPath = CreateInstallationKeyPath(envDirectory);
        try
        {
            File.WriteAllText(Path.Combine(envDirectory, ".env.template"), "Editor__Value=template\n");
            var document = EnvEditorPage.SerializeStructuredDocument(
                [new Supprocom.Secrets.SupprocomSecretSetting("Editor:Value", "saved")]);

            await EnvEditorPage.ReplaceLocalDocumentAsync(envDirectory, document, keyPath);
            var beforeInvalidReplacement = await EnvEditorPage.ReadLocalDocumentAsync(envDirectory, keyPath);
            beforeInvalidReplacement.Should().Contain("Editor__Value=\"saved\"");

            Func<Task> invalidReplacement = () =>
                EnvEditorPage.ReplaceLocalDocumentAsync(envDirectory, "{ invalid dotenv", keyPath);

            var exception = await invalidReplacement.Should().ThrowAsync<SupprocomSecretsException>();
            exception.Which.Code.Should().NotBeNullOrWhiteSpace();
            (await EnvEditorPage.ReadLocalDocumentAsync(envDirectory, keyPath))
                .Should().Be(beforeInvalidReplacement);
            AssertTempInstallationKey(keyPath);
        }
        finally
        {
            DeleteTempDirectory(envDirectory);
        }
    }

    [Test]
    public async Task LocalEditorStore_DoesNotInventDocumentWhenPackageReadFails()
    {
        var envDirectory = CreateTempDirectory();
        var keyPath = CreateInstallationKeyPath(envDirectory);
        try
        {
            var activePath = Path.Combine(envDirectory, ".env");
            var invalidDocument = "{ invalid dotenv";
            File.WriteAllText(activePath, invalidDocument);

            Func<Task> act = () => EnvEditorPage.ReadLocalDocumentAsync(envDirectory, keyPath);

            var exception = await act.Should().ThrowAsync<SupprocomSecretsException>();
            exception.Which.Code.Should().Be("RecoveryTemplateMissing");
            File.ReadAllText(activePath).Should().Be(invalidDocument);
            AssertTempInstallationKey(keyPath);
        }
        finally
        {
            DeleteTempDirectory(envDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "EnvEditor",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateInstallationKeyPath(string envDirectory)
    {
        var keyPath = Path.Combine(envDirectory, "secrets", "encryption-key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        return keyPath;
    }

    private static void AssertTempInstallationKey(string keyPath)
    {
        File.Exists(keyPath).Should().BeTrue(
            "the editor package store must use the test-owned installation key path");
        new FileInfo(keyPath).Length.Should().Be(32);
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
