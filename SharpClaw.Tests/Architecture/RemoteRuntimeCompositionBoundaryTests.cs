using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;

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
        bridge.Should().Contain("MapForwarder");
        bridge.Should().Contain("UseHttps");
    }

    [Test]
    public void Bridge_configuration_must_not_be_authorization()
    {
        var source = ReadRepositoryFile(
            "SharpClaw.Gateway/RemoteRuntimeBridge/RemoteRuntimeBridgeHost.cs");

        source.Should().Contain("RequireActiveTargetAsync");
        source.IndexOf("RequireActiveTargetAsync", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("RemoteRuntimeBridgeHost.Build", StringComparison.Ordinal));
        source.Should().Contain("RequireActiveCertificateAsync");
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
        var paths = CreateGatewayPaths();

        RemoteRuntimeBridgeHost.RegisterServices(services, options, paths);

        services.Should().NotContain(
            descriptor => descriptor.ServiceType == typeof(IRemoteRuntimeBridgeListener));
    }

    [Test]
    public void Enabled_bridge_registration_is_explicit_and_separate()
    {
        var services = new ServiceCollection();
        var options = new RemoteRuntimeBridgeOptions { Enabled = true };
        var paths = CreateGatewayPaths();

        RemoteRuntimeBridgeHost.RegisterServices(services, options, paths);

        services.Should().ContainSingle(
            descriptor => descriptor.ServiceType == typeof(IRemoteRuntimeBridgeListener));
    }

    [Test]
    public void Bridge_configuration_alone_cannot_authorize_binding()
    {
        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ServerCertificatePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "bridge.pfx"),
        };

        var action = () => RemoteRuntimeBridgeHost.Build([], options);

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Bridge_options_do_not_contain_target_or_authoritative_key_configuration()
    {
        var source = ReadRepositoryFile(
            "SharpClaw.Gateway/Configuration/RemoteRuntimeBridgeOptions.cs");

        source.Should().NotContain("TargetBaseUrl");
        source.Should().NotContain("AuthoritativeApiKey");
        source.Should().NotContain("PairingFile");
    }

    [Test]
    public void Bridge_target_comes_from_selected_runtime_discovery_state()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-target-" + Guid.NewGuid().ToString("N"));
        var sharedRoot = Path.Combine(root, "shared");

        try
        {
            var gatewayPaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Gateway,
                Path.Combine(root, "gateway"),
                sharedRoot);
            gatewayPaths.EnsureDirectories();
            var manifest = gatewayPaths.Manifest;
            manifest.SelectedBackendInstanceId = "runtime-selected";
            gatewayPaths.SaveManifest(manifest);

            var runtimeDirectory = Path.Combine(root, "runtime");
            var keyPath = Path.Combine(runtimeDirectory, ".api-key");
            Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllText(keyPath, "runtime-api-key");

            var discoveryDirectory = Path.Combine(sharedRoot, "discovery", "instances");
            Directory.CreateDirectory(discoveryDirectory);
            var entry = new SharpClawDiscoveryEntry
            {
                InstanceKind = SharpClawInstanceKind.Backend,
                InstanceId = "runtime-selected",
                InstallFingerprint = "runtime-fingerprint",
                InstanceRoot = runtimeDirectory,
                BaseUrl = "https://127.0.0.1:48923",
                RuntimeDirectory = runtimeDirectory,
                ApiKeyFilePath = keyPath,
                ProcessId = Environment.ProcessId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
            };
            File.WriteAllText(
                Path.Combine(discoveryDirectory, "backend-runtime-selected.json"),
                JsonSerializer.Serialize(entry));

            var target = RemoteRuntimeBridgeTargetResolver.Resolve(gatewayPaths);

            target.GatewayInstanceId.Should().Be(manifest.InstanceId);
            target.AuthoritativeRuntimeInstanceId.Should().Be("runtime-selected");
            target.TargetBaseUrl.Should().Be("https://127.0.0.1:48923");
            target.AuthoritativeApiKey.Should().Be("runtime-api-key");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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

    private static SharpClawInstancePaths CreateGatewayPaths()
        => new(
            SharpClawInstanceKind.Gateway,
            Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "bridge-boundary-" + Guid.NewGuid().ToString("N")));
}
