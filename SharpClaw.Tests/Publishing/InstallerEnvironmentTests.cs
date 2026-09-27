using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Publishing;

[TestFixture]
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
}
