using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class RemoteRuntimeCompositionBoundaryTests
{
    [Test]
    public void Runtime_mode_selection_must_precede_persistence_and_cli_work()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/Program.cs");

        var launcherIndex = source.IndexOf("RuntimeLauncher", StringComparison.Ordinal);
        var localHostIndex = source.IndexOf("LocalRuntimeHost", StringComparison.Ordinal);

        launcherIndex.Should().BeGreaterThanOrEqualTo(0);
        launcherIndex.Should().BeLessThan(localHostIndex);

        var localSource = ReadRepositoryFile("SharpClaw.Runtime/Host/LocalRuntimeHost.cs");
        localSource.IndexOf("new DurableSegmentStore", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0);
        localSource.IndexOf("TryHandleAsync", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void Runtime_must_have_separate_local_and_remote_composition_types()
    {
        ReadRepositoryFile("SharpClaw.Runtime/Host/LocalRuntimeHost.cs")
            .Should().Contain("LocalRuntimeHost");
        ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs")
            .Should().Contain("RemoteProxyHost");
        ReadRepositoryFile("SharpClaw.Runtime/Host/RuntimePairingClient.cs")
            .Should().Contain("RuntimePairingClient");
    }

    [Test]
    public void Remote_proxy_composition_must_not_initialize_application_services()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs");

        source.Should().NotContain("SharpClawDbContext");
        source.Should().NotContain("AddInfrastructure");
        source.Should().NotContain("DurableSegmentStore");
        source.Should().NotContain("ModuleLoader");
        source.Should().NotContain("ProviderApiClientFactory");
        source.Should().NotContain("CliDispatcher");
        source.Should().NotContain("AddHostedService");
    }

    [Test]
    public void Remote_proxy_must_require_approved_pairing_before_binding()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs");

        source.Should().Contain("Approved");
        source.Should().Contain("Pairing");
        source.Should().Contain("RequireApproved");
        source.Should().NotContain("WebApplication.CreateBuilder");
        source.Should().NotContain("Listen");
    }

    [Test]
    public void Gateway_bridge_must_use_a_separate_opt_in_pipeline()
    {
        var program = ReadRepositoryFile("SharpClaw.Gateway/Program.cs");
        var bridge = ReadRepositoryFile(
            "SharpClaw.Gateway/RemoteRuntimeBridge/RemoteRuntimeBridgeHost.cs");

        program.Should().Contain("RemoteRuntimeBridgeHost");
        program.Should().Contain("RemoteRuntimeBridge");
        program.Should().NotContain(
            "MapRemoteRuntimeBridge",
            "the public Gateway pipeline must not own bridge routes");
        bridge.Should().Contain("MapRemoteRuntimeBridge");
        bridge.Should().Contain("UseHttps");
    }

    [Test]
    public void Bridge_configuration_must_not_be_authorization()
    {
        var source = ReadRepositoryFile(
            "SharpClaw.Gateway/RemoteRuntimeBridge/RemoteRuntimeBridgeHost.cs");

        source.Should().Contain("RequireApprovedPair");
        source.IndexOf("RequireApprovedPair", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("UseHttps", StringComparison.Ordinal));
    }

    [Test]
    public void Local_mode_must_remain_the_default()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RuntimeLaunchMode.cs");

        source.Should().Contain("Local");
        source.Should().Contain("RemoteProxy");
        source.Should().Contain("null or \"\" or \"Local\" => RuntimeLaunchMode.Local");
    }

    [Test]
    public void Empty_runtime_configuration_resolves_to_local_mode()
    {
        var plan = RuntimeLaunchPlan.From(
            [],
            new ConfigurationBuilder().Build());

        plan.Mode.Should().Be(RuntimeLaunchMode.Local);
    }

    [Test]
    public async Task Remote_proxy_without_an_approved_pair_fails_before_binding()
    {
        var plan = new RuntimeLaunchPlan(RuntimeLaunchMode.RemoteProxy, null, null);

        var action = async () => await RemoteProxyHost.RunAsync(plan);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public void Disabled_bridge_registration_does_not_change_gateway_services()
    {
        var services = new ServiceCollection();
        var options = new RemoteRuntimeBridgeOptions { Enabled = false };

        RemoteRuntimeBridgeHost.RegisterServices(services, options);

        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IRemoteRuntimeBridgeListener));
    }

    [Test]
    public void Enabled_bridge_registration_is_explicit_and_separate()
    {
        var services = new ServiceCollection();
        var options = new RemoteRuntimeBridgeOptions { Enabled = true };

        RemoteRuntimeBridgeHost.RegisterServices(services, options);

        services.Should().ContainSingle(
            descriptor => descriptor.ServiceType == typeof(IRemoteRuntimeBridgeListener));
    }

    [Test]
    public void Bridge_configuration_alone_cannot_authorize_binding()
    {
        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            PairingFile = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pairing.json"),
            ServerCertificatePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "bridge.pfx"),
            TargetBaseUrl = "http://127.0.0.1:48923",
            AuthoritativeApiKey = "configuration-is-not-approval",
        };

        var action = () => RemoteRuntimeBridgeHost.Build([], options);

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Both_forwarding_hops_use_the_approved_Yarp_package()
    {
        var props = ReadRepositoryFile("Directory.Packages.props");
        var gatewayProject = ReadRepositoryFile("SharpClaw.Gateway/SharpClaw.Gateway.csproj");
        var runtimeProject = ReadRepositoryFile(
            "SharpClaw.Runtime/Host/SharpClaw.Runtime.Host.csproj");
        var gatewaySource = ReadRepositoryFile(
            "SharpClaw.Gateway/RemoteRuntimeBridge/RemoteRuntimeBridgeHost.cs");
        var proxySource = ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs");

        props.Should().Contain("Yarp.ReverseProxy\" Version=\"2.3.0");
        gatewayProject.Should().Contain("PackageReference Include=\"Yarp.ReverseProxy\"");
        runtimeProject.Should().Contain("PackageReference Include=\"Yarp.ReverseProxy\"");
        gatewaySource.Should().Contain("MapForwarder");
        proxySource.Should().Contain("MapForwarder");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
