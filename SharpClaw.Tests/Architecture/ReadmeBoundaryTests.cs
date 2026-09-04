using System.Reflection;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class ReadmeBoundaryTests
{
    [Test]
    public void Readme_describes_the_kernel_registration_and_storage_boundaries()
    {
        var root = FindSourceRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        readme.Should().Contain("## Kernel By Default");
        readme.Should().Contain("## Modules And Capabilities");
        readme.Should().Contain("| Module type | Capability when enabled |");
        readme.Should().Contain("## Bring Your Own Features");
        readme.Should().Contain("| Extension surface | What you can supply |");
        readme.Should().Contain("JSONColdStore");
        readme.Should().Contain("PostgreSQL");
        readme.Should().Contain("SQL Server");
        readme.Should().Contain("SQLite");
        readme.Should().Contain("https://github.com/SharpClaw-NET/SharpClaw");
        readme.Should().NotContain("Development Status");
        readme.Should().NotContain("We're Hiring");
        readme.Should().NotContain("Disclaimer");
        readme.Should().NotContain("github.com/mkn8rn/SharpClaw");
    }

    private static string FindSourceRoot()
    {
        var starts = new[]
        {
            Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT"),
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        };

        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(start!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find the SharpClaw source root.");
    }
}
