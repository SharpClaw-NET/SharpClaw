using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
internal sealed class RemoteGatewayConnectionTests
{
    [TestCase("https://peer.example/", "https://peer.example/api/")]
    [TestCase("https://peer.example/api", "https://peer.example/api/")]
    [TestCase("https://peer.example/api/", "https://peer.example/api/")]
    [TestCase("https://peer.example/tenant", "https://peer.example/tenant/api/")]
    [TestCase("https://peer.example/tenant/api/", "https://peer.example/tenant/api/")]
    [TestCase("http://127.0.0.1:48924/", "http://127.0.0.1:48924/api/")]
    [TestCase("http://localhost:48924/", "http://localhost:48924/api/")]
    [TestCase("http://[::1]:48924/", "http://[::1]:48924/api/")]
    [TestCase("http://peer.example/", "http://peer.example/api/")]
    [TestCase("http://192.0.2.1/", "http://192.0.2.1/api/")]
    public void GatewayAddressResolvesRequestsInsideItsApiPrefix(string address, string expectedPrefix)
    {
        var connection = RemoteGatewayConnection.Create(new Uri(address), null);
        connection.GatewayBaseUri.Should().Be(new Uri(expectedPrefix));
        new Uri(connection.GatewayBaseUri, "chat?stream=true")
            .Should().Be(new Uri(expectedPrefix + "chat?stream=true"));
    }

    [TestCase("file:///tmp/peer")]
    [TestCase("ftp://peer.example/")]
    [TestCase("https://name:credential-sentinel@peer.example/")]
    [TestCase("https://peer.example/?token=credential-sentinel")]
    [TestCase("https://peer.example/#credential-sentinel")]
    public void UnsafeGatewayAddressIsRejectedWithoutRenderingEmbeddedSecrets(string address)
    {
        Action create = () => RemoteGatewayConnection.Create(new Uri(address), null);
        var failure = create.Should().Throw<ArgumentException>().Which;
        failure.Message.Should().NotContain("credential-sentinel");
    }

    [Test]
    public void RelativeGatewayAddressCannotAcquireAnUpstreamAuthority()
    {
        Action create = () => RemoteGatewayConnection.Create(new Uri("api/", UriKind.Relative), null);
        create.Should().Throw<ArgumentException>();
    }

    [TestCase("http://peer.example/")]
    [TestCase("http://192.0.2.1/")]
    public void RemoteHttpCannotCarryABearerCredential(string address)
    {
        Action create = () => RemoteGatewayConnection.Create(new Uri(address), "credential-sentinel");
        var failure = create.Should().Throw<ArgumentException>().Which;
        failure.Message.Should().NotContain("credential-sentinel");
    }

    [TestCase("token with spaces")]
    [TestCase("credential-sentinel\r\nInjected: value")]
    [TestCase("credential-sentinel\u0000")]
    [TestCase("credential-sentinel\u2028")]
    public void InvalidBearerCredentialIsRejectedWithoutRenderingItsValue(string credential)
    {
        Action create = () => RemoteGatewayConnection.Create(new Uri("https://peer.example/"), credential);
        var failure = create.Should().Throw<ArgumentException>().Which;
        failure.Message.Should().NotContain("credential-sentinel");
    }

    [Test]
    public void ConnectionSerializationAndDisplayNeverPublishTheTransportCredential()
    {
        var connection = RemoteGatewayConnection.Create(new Uri("https://peer.example/"), "  remote-secret-sentinel  ");
        connection.AccessToken.Should().Be("remote-secret-sentinel");
        // Ordinary object display is the public contract being checked, including its current default implementation.
#pragma warning disable MA0150
        connection.ToString().Should().NotContain("remote-secret-sentinel");
#pragma warning restore MA0150
        var json = JsonSerializer.Serialize(connection);
        json.Should().NotContain("remote-secret-sentinel");
        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty(nameof(RemoteGatewayConnection.AccessToken), out _).Should().BeFalse();
        Uri.TryCreate(document.RootElement.GetProperty(nameof(RemoteGatewayConnection.GatewayBaseUri)).GetString(),
            UriKind.Absolute, out var serializedUri).Should().BeTrue();
        serializedUri.Should().Be(new Uri("https://peer.example/api/"));
    }

    [TestCase(null), TestCase("false")]
    public void DisabledProxyDoesNotInterpretOrUseRetainedRemoteConfiguration(string? enabled)
    {
        var configuration = Configuration(enabled, "not a URI", "invalid credential");
        RemoteGatewayConnection.FromConfiguration(configuration).Should().BeNull();
    }

    [TestCase(""), TestCase("yes"), TestCase("1")]
    public void InvalidEnablementCannotSilentlySelectLocalExecution(string enabled)
    {
        Action read = () => RemoteGatewayConnection.FromConfiguration(
            Configuration(enabled, "https://peer.example/", null));
        read.Should().Throw<InvalidDataException>();
    }

    [TestCase(null), TestCase(""), TestCase("not a URI")]
    public void EnabledProxyWithoutAValidAddressCannotSilentlySelectLocalExecution(string? address)
    {
        Action read = () => RemoteGatewayConnection.FromConfiguration(Configuration("true", address, null));
        read.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void EnabledConfigurationRetainsOnlyItsExplicitRemoteCredential()
    {
        var connection = RemoteGatewayConnection.FromConfiguration(
            Configuration("true", "https://peer.example/tenant", "remote-access"));
        connection.Should().NotBeNull();
        connection!.GatewayBaseUri.Should().Be(new Uri("https://peer.example/tenant/api/"));
        connection.AccessToken.Should().Be("remote-access");
    }

    private static IConfigurationRoot Configuration(string? enabled, string? address, string? credential) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RemoteBackend:Enabled"] = enabled,
            ["RemoteBackend:GatewayUrl"] = address,
            ["RemoteBackend:AccessToken"] = credential,
            ["Provider:ApiKey"] = "local-provider-key",
            ["InternalApi:ApiKey"] = "local-runtime-key",
        }).Build();
}
