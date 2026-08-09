using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Runtime.BLL.Kernel;
using System.Xml.Linq;

namespace SharpClaw.Tests.Architecture;

/// <summary>
/// Guardrail test that prevents <c>SharpClaw.Runtime.BLL</c> from
/// re-acquiring a project reference to either provider shared library.
/// The pipeline must remain agnostic to whether a model is local or
/// remote and to any provider-specific protocol shape; everything that
/// previously needed those references has been hoisted onto
/// <c>IProviderPlugin</c> in <c>SharpClaw.Contracts.Providers</c>.
/// </summary>
[TestFixture]
public class CoreDependencyGuardrailTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "SharpClaw.Providers.Common",
        "SharpClaw.Providers.LocalCommon",
    ];

    [Test]
    public void Core_assembly_must_not_reference_provider_shared_libraries()
    {
        var coreAssembly = typeof(DirectChatKernel).Assembly;

        var referenced = coreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in ForbiddenAssemblies)
        {
            referenced.Should().NotContain(forbidden,
                because: $"SharpClaw.Runtime.BLL must not reference '{forbidden}'. "
                       + "Provider-shape concerns belong on IProviderPlugin in "
                       + "SharpClaw.Contracts.Providers, not in pipeline code.");
        }
    }

    [Test]
    public void Runtime_BLL_compiles_only_the_canonical_kernel_surface()
    {
        var root = FindSolutionRoot();
        var project = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "BLL",
            "SharpClaw.Runtime.BLL.csproj"));
        var includes = project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => include!)
            .ToArray();

        includes.Should().Contain("Kernel\\**\\*.cs");
        includes.Should().NotContain(include => include.Contains("Services", StringComparison.OrdinalIgnoreCase));
        includes.Should().NotContain(include => include.Contains("Modules", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindSolutionRoot()
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

        throw new DirectoryNotFoundException("Could not locate SharpClaw.slnx from test assembly.");
    }
}
