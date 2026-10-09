using System.Reflection;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Tests.Gateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class GatewayCurrentSurfaceTests
{
    [Test]
    public void Endpoint_options_expose_only_the_gateway_switch()
    {
        typeof(GatewayEndpointOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should()
            .Equal(nameof(GatewayEndpointOptions.Enabled));
    }
}
