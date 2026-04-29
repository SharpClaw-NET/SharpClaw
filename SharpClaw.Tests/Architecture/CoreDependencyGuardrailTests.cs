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
}
