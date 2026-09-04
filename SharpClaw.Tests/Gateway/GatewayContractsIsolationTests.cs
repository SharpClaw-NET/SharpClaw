using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Gateway.Contracts;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Enforces the safety rail that the gateway contracts assembly
/// stays free of any reference to the gateway implementation.
/// </summary>
[TestFixture]
public sealed class GatewayContractsIsolationTests
{
    [Test]
    public void ContractsAssembly_DoesNotReferenceGatewayImplementation()
    {
        var contractsAssembly = typeof(IGatewayDispatcher).Assembly;

        var referencedNames = contractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referencedNames.Should().NotContain("SharpClaw.Gateway",
            because: "the contracts assembly must not pull the gateway implementation into its compilation closure.");
    }

    [Test]
    public void ContractsAssembly_OnlyReferencesFrameworkAndBclAssemblies()
    {
        var contractsAssembly = typeof(IGatewayDispatcher).Assembly;

        var nonFrameworkRefs = contractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                        && !name.StartsWith("Microsoft.", StringComparison.Ordinal)
                        && name != "netstandard"
                        && name != "mscorlib")
            .ToArray();

        nonFrameworkRefs.Should().BeEmpty(
            because: "the transport contract must contain only framework and BCL dependencies.");
    }
}
