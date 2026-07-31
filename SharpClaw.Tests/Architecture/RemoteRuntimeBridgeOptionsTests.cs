using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class RemoteRuntimeBridgeOptionsTests
{
    [Test]
    public void Enabled_bridge_reads_bounded_concurrency_limits()
    {
        var options = RemoteRuntimeBridgeOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:RemoteRuntimeBridge:Enabled"] = "true",
                    ["Gateway:RemoteRuntimeBridge:MaxConcurrentRequestsPerPair"] = "12",
                    ["Gateway:RemoteRuntimeBridge:MaxConcurrentStreamsPerPair"] = "3",
                    ["Gateway:RemoteRuntimeBridge:MaxConcurrentWebSocketsPerPair"] = "2",
                })
                .Build());

        options.MaxConcurrentRequestsPerPair.Should().Be(12);
        options.MaxConcurrentStreamsPerPair.Should().Be(3);
        options.MaxConcurrentWebSocketsPerPair.Should().Be(2);
    }

    [Test]
    public void Invalid_enabled_limit_fails_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:RemoteRuntimeBridge:Enabled"] = "true",
                ["Gateway:RemoteRuntimeBridge:MaxConcurrentStreamsPerPair"] = "0",
            })
            .Build();

        var action = () => RemoteRuntimeBridgeOptions.FromConfiguration(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxConcurrentStreamsPerPair*");
    }

    [Test]
    public void Disabled_bridge_does_not_validate_unused_limits()
    {
        var options = RemoteRuntimeBridgeOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:RemoteRuntimeBridge:Enabled"] = "false",
                    ["Gateway:RemoteRuntimeBridge:MaxConcurrentRequestsPerPair"] = "0",
                })
                .Build());

        options.Enabled.Should().BeFalse();
        options.MaxConcurrentRequestsPerPair.Should().Be(64);
    }
}
