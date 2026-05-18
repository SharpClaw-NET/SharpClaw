using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

/// <summary>
/// Guardrail test that prevents <c>SharpClaw.Application.Core</c> from
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
        var coreAssembly = typeof(SharpClaw.Application.Services.AgentService).Assembly;

        var referenced = coreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in ForbiddenAssemblies)
        {
            referenced.Should().NotContain(forbidden,
                because: $"SharpClaw.Application.Core must not reference '{forbidden}'. "
                       + "Provider-shape concerns belong on IProviderPlugin in "
                       + "SharpClaw.Contracts.Providers, not in pipeline code.");
        }
    }

    [Test]
    public void Job_pipeline_must_not_reference_json_index_implementation()
    {
        var root = FindSolutionRoot();
        var pipelineFiles = new[]
        {
            Path.Combine(root, "SharpClaw.Application.Core", "Services", "AgentJobService.cs"),
            Path.Combine(root, "SharpClaw.Application.Core", "Modules", "HostAgentJobController.cs"),
            Path.Combine(root, "SharpClaw.Application.API", "Handlers", "AgentJobHandlers.cs"),
        };
        var forbiddenTerms = new[]
        {
            "SharpClaw.Infrastructure.Persistence.JSON",
            "ColdEntityIndex",
            "ColdEntityStore",
            "_index_AgentJobId",
        };

        var offenders = pipelineFiles
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return forbiddenTerms
                    .Where(term => text.Contains(term, StringComparison.Ordinal))
                    .Select(term => $"{Path.GetRelativePath(root, path)} contains {term}");
            })
            .ToList();

        offenders.Should().BeEmpty(
            "job orchestration business logic must stay on persistence abstractions, "
            + "with JSON file indexes kept inside the infrastructure provider");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw.slnx from test assembly.");
    }
}
