using System.Diagnostics.CodeAnalysis;
using SharpClaw.Shared.Security;
using SharpClaw.Services;

namespace SharpClaw.Tests.Publishing;

[TestFixture]
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "NUnit discovers and instantiates this test fixture through reflection.")]
internal sealed class InstallerEnvironmentTests
{
    [Test]
    public void WritableConfigurationCopiesOnlyTemplatesAndPreservesExistingConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpclaw-environment-test-{Guid.NewGuid():N}");
        var binaries = Path.Combine(root, "binaries");
        var writable = Path.Combine(root, "state", "config");
        Directory.CreateDirectory(binaries);
        try
        {
            File.WriteAllText(Path.Combine(binaries, ".env.template"), "Provider__Key=template\n");
            File.WriteAllText(Path.Combine(binaries, ".env"), "Provider__ApiKey=must-not-copy\n");
            SharpClawEnvironmentDirectory.Prepare(binaries, writable).Should().Be(writable);
            File.ReadAllText(Path.Combine(writable, ".env.template")).Should().Contain("template");
            File.Exists(Path.Combine(writable, ".env")).Should().BeFalse();
            File.WriteAllText(Path.Combine(writable, ".env"), "existing-protected-config");
            File.WriteAllText(Path.Combine(writable, ".env.template"), "administrator-template");
            SharpClawEnvironmentDirectory.Prepare(binaries, writable);
            File.ReadAllText(Path.Combine(writable, ".env")).Should().Be("existing-protected-config");
            File.ReadAllText(Path.Combine(writable, ".env.template")).Should().Be("administrator-template");
            File.ReadAllText(Path.Combine(binaries, ".env")).Should().Contain("must-not-copy");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void InstallerConfigurationRequiresAnAbsoluteDirectory()
    {
        Action prepare = () => SharpClawEnvironmentDirectory.Prepare("unused", "relative/config");
        prepare.Should().Throw<InvalidOperationException>();
    }

    [TestCase(".env.template")]
    [TestCase(".dev.env.template")]
    public void TemplateSeedingPreservesBytesWithoutSourceFileAttributes(string templateName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpclaw-template-content-test-{Guid.NewGuid():N}");
        var binaries = Path.Combine(root, "binaries");
        var writable = Path.Combine(root, "config");
        Directory.CreateDirectory(binaries);
        var source = Path.Combine(binaries, templateName);
        byte[] contents = [0xef, 0xbb, 0xbf, 0x23, 0x20, 0xc5, 0xa1, 0x0d, 0x0a, 0x00];
        try
        {
            File.WriteAllBytes(source, contents);
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);

            SharpClawEnvironmentDirectory.Prepare(binaries, writable);

            var destination = Path.Combine(writable, templateName);
            File.ReadAllBytes(destination).Should().Equal(contents);
            (File.GetAttributes(destination) & (FileAttributes.ReadOnly | FileAttributes.Encrypted))
                .Should().Be((FileAttributes)0);
            File.WriteAllText(destination, "administrator template");
            SharpClawEnvironmentDirectory.Prepare(binaries, writable);
            File.ReadAllText(destination).Should().Be("administrator template");
            File.ReadAllBytes(source).Should().Equal(contents);
            Directory.GetFiles(writable, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(source))
                File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void MsixStateRootDoesNotDependOnTheVersionedInstallationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpclaw-install-profile-test-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "package-0.5.0.1");
        var second = Path.Combine(root, "package-0.5.0.2");
        var shared = Path.Combine(root, "user-state");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "sharpclaw-installation.json"), "{}");
            File.WriteAllText(Path.Combine(second, "sharpclaw-installation.json"), "{}");
            var initial = FrontendInstanceService.ResolveInstalledFrontendRoot(first, shared, isWindows: true);
            var upgraded = FrontendInstanceService.ResolveInstalledFrontendRoot(second, shared, isWindows: true);
            initial.Should().Be(upgraded);
            initial.Should().StartWith(shared);
            FrontendInstanceService.ResolveInstalledFrontendRoot(first, shared, isWindows: false).Should().BeNull();
            FrontendInstanceService.ResolveInstalledFrontendRoot(root, shared, isWindows: true).Should().BeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
