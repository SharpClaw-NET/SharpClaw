using System.Reflection;
using System.Text.Json;
using System.Net.Http;
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
        localSource.Should().Contain("DirectChatKernelFactory");
        localSource.Should().Contain("KernelHostEndpoints.Map");
        localSource.Should().NotContain("TryHandleAsync");
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
        source.Should().Contain("LoadApprovedSessionAsync");
        source.Should().NotContain("WebApplication.CreateBuilder");
        source.Should().NotContain("Listen");
    }

    [Test]
    public void Remote_proxy_attempts_configured_pairing_when_session_is_missing()
    {
        var source = ReadRepositoryFile(
            "SharpClaw.Runtime/Host/RemoteRuntimePairingAuthorization.cs");

        source.Should().Contain("RemoteRuntimeProxySessionSecrets.Create");
        source.Should().Contain("await secrets.ReadAsync");
        source.Should().Contain("RuntimePairingClient.PairAsync");
        source.Should().Contain("active approved session");
    }

    [Test]
    public void Remote_proxy_revalidates_stored_session_before_binding()
    {
        var source = ReadRepositoryFile(
            "SharpClaw.Runtime/Host/RemoteRuntimePairingAuthorization.cs");

        source.Should().Contain("RuntimePairingClient.ValidateActiveSessionAsync");
        source.IndexOf("ValidateActiveSessionAsync", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("new RemoteRuntimeProxySession", StringComparison.Ordinal));
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

        source.Should().Contain("PairingClaim");
        source.Should().Contain("PairingCertificate");
        source.Should().Contain("HasLocalAdministrationAccess");
        source.Should().Contain("ClientCertificateMode.AllowCertificate");
        source.Should().Contain("RequireActiveCertificateAsync");
    }

    [Test]
    public void Local_mode_must_remain_the_default()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RuntimeLaunchMode.cs");

        source.Should().Contain("Local");
        source.Should().Contain("RemoteProxy");
        source.Should().Contain("RemoteProxyOptions.Bind");
        source.Should().NotContain("Runtime:Mode");
        source.Should().NotContain("SHARPCLAW_RUNTIME_MODE");
        source.Should().NotContain("PairingFile");
    }

    [Test]
    public void Empty_runtime_configuration_resolves_to_local_mode()
    {
        var plan = RuntimeLaunchPlan.From(
            [],
            new ConfigurationBuilder().Build());

        plan.Mode.Should().Be(RuntimeLaunchMode.Local);
        plan.RemoteProxyOptions.Should().BeNull();
    }

    [Test]
    public void Explicit_remote_disablement_restores_local_mode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:RemoteProxy:Enabled"] = "false",
                ["Runtime:RemoteProxy:LocalUrl"] = "not-a-url",
            })
            .Build();

        var plan = RuntimeLaunchPlan.From([], configuration);

        plan.Mode.Should().Be(RuntimeLaunchMode.Local);
        plan.RemoteProxyOptions.Should().BeNull();
    }

    [Test]
    public void Complete_remote_options_select_remote_proxy_mode()
    {
        var plan = RuntimeLaunchPlan.From([], CreateRemoteConfiguration());

        plan.Mode.Should().Be(RuntimeLaunchMode.RemoteProxy);
        plan.RequireRemoteProxyOptions().LocalUrl.Should().Be("http://127.0.0.1:48923");
    }

    [Test]
    public void Pairing_composition_reads_invitation_inputs_from_remote_options()
    {
        var plan = RuntimeLaunchPlan.From(["--pair"], CreateRemoteConfiguration());

        plan.Mode.Should().Be(RuntimeLaunchMode.PairingClient);
        plan.RequireRemoteProxyOptions().CreateInvitation().Secret.Should().Be("secret-name");
    }

    [Test]
    public void Partial_remote_options_fail_closed_before_host_selection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:RemoteProxy:Enabled"] = "true",
                ["Runtime:RemoteProxy:LocalUrl"] = "http://127.0.0.1:48923",
            })
            .Build();

        var action = () => RuntimeLaunchPlan.From([], configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*GatewayUrl*");
    }

    [Test]
    public void Legacy_mode_configuration_cannot_authorize_proxy_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:Mode"] = "RemoteProxy",
                ["SHARPCLAW_RUNTIME_MODE"] = "RemoteProxy",
            })
            .Build();

        var plan = RuntimeLaunchPlan.From([], configuration);

        plan.Mode.Should().Be(RuntimeLaunchMode.Local);
    }

    [Test]
    public void Pairing_composition_requires_remote_options()
    {
        var action = () => RuntimeLaunchPlan.From(
            ["--pair"],
            new ConfigurationBuilder().Build());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Runtime:RemoteProxy*");
    }

    [Test]
    public void Early_launcher_uses_the_local_environment_loader_without_local_host_services()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RuntimeLauncher.cs");

        source.Should().Contain("AddLocalEnvironment");
        source.Should().Contain("RuntimeLaunchPlan.From");
        source.Should().NotContain("AddInfrastructure");
        source.Should().NotContain("LocalRuntimeHost");
        source.IndexOf("RuntimeLaunchPlan.From", StringComparison.Ordinal)
            .Should().BeGreaterThan(source.IndexOf("AddLocalEnvironment", StringComparison.Ordinal));
    }

    [Test]
    public async Task Remote_proxy_unreachable_pairing_fails_closed_before_binding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(CreateRemoteConfigurationValues())
            {
                ["Runtime:RemoteProxy:GatewayUrl"] = "https://127.0.0.1:1",
            })
            .Build();
        var plan = RuntimeLaunchPlan.From([], configuration);

        var action = async () => await RemoteProxyHost.RunAsync(plan);

        await action.Should().ThrowAsync<HttpRequestException>();
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
            var gatewayTokenPath = Path.Combine(runtimeDirectory, ".gateway-token");
            Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllText(keyPath, "runtime-api-key");
            File.WriteAllText(gatewayTokenPath, "gateway-service-token");

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
                GatewayTokenFilePath = gatewayTokenPath,
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
            target.AuthoritativeGatewayToken.Should().Be("gateway-service-token");

            entry.BaseUrl = "https://10.0.0.8:48923";
            File.WriteAllText(
                Path.Combine(discoveryDirectory, "backend-runtime-selected.json"),
                JsonSerializer.Serialize(entry));
            var nonLoopback = () => RemoteRuntimeBridgeTargetResolver.Resolve(gatewayPaths);
            nonLoopback.Should().Throw<InvalidOperationException>()
                .WithMessage("*loopback*");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Bridge_rejects_nonloopback_authoritative_target_before_binding()
    {
        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            "https://10.0.0.8:48923",
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(target);
        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = "https://127.0.0.1:48925",
            ServerCertificatePath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "missing-bridge-certificate.pfx"),
        };

        var action = async () => await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            options,
            registryClient,
            target);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*loopback*");
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

    [Test]
    public void Proxy_forwarding_uses_the_paired_certificate_and_strips_local_credentials()
    {
        var source = ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs");
        var gatewayBridgeSource = ReadRepositoryFile(
            "SharpClaw.Gateway/RemoteRuntimeBridge/RemoteRuntimeBridgeHost.cs");

        source.Should().Contain("ClientCertificateForwarderHttpClientFactory");
        source.Should().Contain("ClientCertificates");
        source.Should().Contain("GatewayServerPublicKeyHash");
        source.Should().Contain("HasPinnedPublicKey");
        source.Should().Contain("RemoteRuntimeCertificateHash.Compute");
        source.Should().Contain("PublishDiscovery");
        source.Should().Contain("proxyRequest.Headers.Remove(\"X-Api-Key\")");
        source.Should().Contain("proxyRequest.Headers.Remove(\"X-Gateway-Token\")");
        gatewayBridgeSource.Should().Contain("RemoteRuntimeBridgePaths.CliControl");
        gatewayBridgeSource.Should().Contain("authoritativeGatewayToken");
        gatewayBridgeSource.Should().Contain("proxyRequest.Headers.TryAddWithoutValidation(\n                    \"X-Gateway-Token\",");
    }

    [Test]
    public void Proxy_transport_uses_configured_connect_and_activity_timeouts()
    {
        var proxySource = ReadRepositoryFile("SharpClaw.Runtime/Host/RemoteProxyHost.cs");
        var pairingSource = ReadRepositoryFile("SharpClaw.Runtime/Host/RuntimePairingClient.cs");

        proxySource.Should().Contain("handler.ConnectTimeout = connectTimeout");
        proxySource.Should().Contain("ActivityTimeout = connection.ActivityTimeout");
        pairingSource.Should().Contain("ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds)");
        pairingSource.Should().Contain("Timeout = TimeSpan.FromSeconds(activityTimeoutSeconds)");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var starts = new[]
        {
            Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT"),
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        };

        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(start!);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private static SharpClawInstancePaths CreateGatewayPaths()
        => new(
            SharpClawInstanceKind.Gateway,
            Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "bridge-boundary-" + Guid.NewGuid().ToString("N")));

    private static IConfiguration CreateRemoteConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(CreateRemoteConfigurationValues())
            .Build();

    private static Dictionary<string, string?> CreateRemoteConfigurationValues()
        => new()
        {
                ["Runtime:RemoteProxy:Enabled"] = "true",
                ["Runtime:RemoteProxy:LocalUrl"] = "http://127.0.0.1:48923",
                ["Runtime:RemoteProxy:GatewayUrl"] = "https://gateway.example:48925",
                ["Runtime:RemoteProxy:GatewayInstanceId"] = "gateway-1",
                ["Runtime:RemoteProxy:AuthoritativeRuntimeInstanceId"] = "runtime-1",
                ["Runtime:RemoteProxy:ProxyRuntimeInstanceId"] = "proxy-1",
                ["Runtime:RemoteProxy:InvitationSecret"] = "secret-name",
                ["Runtime:RemoteProxy:PrivateKeySecret"] = "private-key-name",
                ["Runtime:RemoteProxy:ClientCertificateSecret"] = "certificate-name",
                ["Runtime:RemoteProxy:ConnectTimeoutSeconds"] = "10",
                ["Runtime:RemoteProxy:ActivityTimeoutSeconds"] = "120",
                ["Runtime:RemoteProxy:InvitationPairId"] = "d2719a1c-4e19-4da3-9b9f-4925c4f26eb5",
                ["Runtime:RemoteProxy:GatewayServerPublicKeyHash"] = "gateway-public-key-hash",
                ["Runtime:RemoteProxy:AuthoritativeRuntimeInstallFingerprint"] = "runtime-fingerprint",
                ["Runtime:RemoteProxy:InvitationExpiresAtUtc"] = "2099-01-01T00:00:00Z",
        };
}
