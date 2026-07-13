using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Contracts;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Enforces the safety rail that the gateway contracts assembly
/// stays free of any reference to the gateway implementation. A regression
/// here would re-couple module code to the gateway process, defeating the
/// whole point of the package split.
/// </summary>
[TestFixture]
public sealed class GatewayContractsIsolationTests
{
    [Test]
    public void ContractsAssembly_DoesNotReferenceGatewayImplementation()
    {
        var contractsAssembly = typeof(IGatewayModuleExtension).Assembly;

        var referencedNames = contractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referencedNames.Should().NotContain("SharpClaw.Gateway",
            because: "the contracts assembly is consumed by modules and must not pull the gateway "
                   + "implementation into their compilation closure.");
    }

    [Test]
    public void ContractsAssembly_OnlyReferencesFrameworkAndBclAssemblies()
    {
        var contractsAssembly = typeof(IGatewayModuleExtension).Assembly;

        var nonFrameworkRefs = contractsAssembly.GetReferencedAssemblies()
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
