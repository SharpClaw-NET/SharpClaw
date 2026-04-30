using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Abstractions;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Enforces the Phase 1 safety rail that the gateway abstractions assembly
/// stays free of any reference to the gateway implementation. A regression
/// here would re-couple module code to the gateway process, defeating the
/// whole point of the abstractions split.
/// </summary>
[TestFixture]
public sealed class GatewayAbstractionsIsolationTests
{
    [Test]
    public void AbstractionsAssembly_DoesNotReferenceGatewayImplementation()
    {
        var abstractionsAssembly = typeof(IGatewayModuleExtension).Assembly;

        var referencedNames = abstractionsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referencedNames.Should().NotContain("SharpClaw.Gateway",
            because: "the abstractions assembly is consumed by modules and must not pull the gateway "
                   + "implementation into their compilation closure.");
    }

    [Test]
    public void AbstractionsAssembly_OnlyReferencesFrameworkAndBclAssemblies()
    {
        var abstractionsAssembly = typeof(IGatewayModuleExtension).Assembly;

        var nonFrameworkRefs = abstractionsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                        && !name.StartsWith("Microsoft.", StringComparison.Ordinal)
                        && name != "netstandard"
                        && name != "mscorlib")
            .ToArray();

        nonFrameworkRefs.Should().BeEmpty(
            because: "Phase 1 keeps the abstractions assembly on framework and BCL types only.");
    }
}
