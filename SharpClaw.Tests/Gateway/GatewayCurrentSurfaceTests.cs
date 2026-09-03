using System.Reflection;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
public sealed class GatewayCurrentSurfaceTests
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
