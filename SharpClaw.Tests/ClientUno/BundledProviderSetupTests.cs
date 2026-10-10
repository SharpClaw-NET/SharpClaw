using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;
using SharpClaw.Runtime.INF.Configuration;
using Supprocom.Secrets;
using RuntimeEnvironment = SharpClaw.Runtime.INF.Configuration.LocalEnvironment;

namespace SharpClaw.Tests.ClientUno;

[TestFixture]
[NonParallelizable]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit instantiates this fixture through reflection.")]
internal sealed class BundledProviderSetupTests
{
    [Test]
    public void Switching_provider_preserves_unrelated_settings_and_does_not_rebind_old_credentials()
    {
        SupprocomSecretSetting[] original =
        [new("Provider:Key", "primary"), new("Provider:ApiKey", "primary-secret"),
         new("Provider:Endpoint", "https://primary.example"), new("Database:Provider", "ThirdParty")];
        var selected = new SharpClawProviderSetupOption("alternate", "Alternate", true, true);
        var result = BundledProviderSetup.UpdateSettings(original, selected, "alternate-model",
            "https://alternate.example", "alternate-secret").ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        result["Providers:primary:ApiKey"].Should().Be("primary-secret");
        result["Providers:primary:Endpoint"].Should().Be("https://primary.example");
        result["Providers:alternate:ApiKey"].Should().Be("alternate-secret");
        result["Provider:Key"].Should().Be("alternate");
        result["Database:Provider"].Should().Be("ThirdParty");
        result.Should().NotContainKey("Provider:ApiKey");
        result.Should().NotContainKey("Provider:Endpoint");
        Action missingCredential = () => BundledProviderSetup.UpdateSettings(original, selected,
            "model", "https://alternate.example", null);
        missingCredential.Should().Throw<InvalidOperationException>();
        original[1].Value.Should().Be("primary-secret");
    }

    [TestCase("file:///tmp/provider")]
    [TestCase("https://user:password@example.test")]
    public void Unsafe_effective_endpoints_are_rejected(string endpoint)
    {
        Action save = () => BundledProviderSetup.UpdateSettings([], new("third-party", "Third Party", false, true),
            "model", endpoint, null);
        save.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task Local_setup_uses_the_existing_protected_document_and_requires_a_new_runtime_generationAsync()
    {
        using var scope = new SetupScope();
        await scope.Backend.EnsureStartedAsync().ConfigureAwait(false);
        await BundledProviderSetup.ApplyAsync(scope.Frontend, scope.Backend, null, new ClientActionDispatcher(),
            new("future-provider", "Future Provider", true, true), "future-model", "https://future.example", "test-secret").ConfigureAwait(false);
        scope.Backend.IsRunning.Should().BeFalse();
        var protectedFile = await File.ReadAllBytesAsync(Path.Combine(scope.BackendPaths.ConfigDirectory, ".env")).ConfigureAwait(false);
        System.Text.Encoding.UTF8.GetString(protectedFile).Should().NotContain("test-secret");
        var config = new ConfigurationBuilder().AddLocalEnvironmentFrom(
            scope.BackendPaths.ConfigDirectory, false, scope.BackendPaths).Build();
        config["Provider:Key"].Should().Be("future-provider");
        config["Provider:Model"].Should().Be("future-model");
        config["Providers:future-provider:ApiKey"].Should().Be("test-secret");
        config["Unrelated:Option"].Should().Be("preserved");
        Directory.EnumerateFiles(scope.Frontend.Paths.ConfigDirectory, ".env").Should().BeEmpty();
    }

    [Test]
    public async Task Invalid_credentials_preserve_the_document_and_do_not_stop_the_running_runtimeAsync()
    {
        using var scope = new SetupScope();
        var store = new SupprocomSecretFileStore(RuntimeEnvironment.CreateSecretsOptions(
            scope.BackendPaths.ConfigDirectory, false, scope.BackendPaths));
        await store.LoadAsync().ConfigureAwait(false);
        var file = Path.Combine(scope.BackendPaths.ConfigDirectory, ".env");
        var before = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        await scope.Backend.EnsureStartedAsync().ConfigureAwait(false);
        Func<Task> save = () => BundledProviderSetup.ApplyAsync(scope.Frontend, scope.Backend, null,
            new ClientActionDispatcher(), new("future-provider", "Future", true, false), "model", null, null);
        await save.Should().ThrowAsync<Exception>().ConfigureAwait(false);
        (await File.ReadAllBytesAsync(file).ConfigureAwait(false)).Should().Equal(before);
        scope.Backend.IsRunning.Should().BeTrue();
    }

    [Test]
    public async Task External_runtime_is_never_written_or_stopped_by_local_setupAsync()
    {
        using var scope = new SetupScope();
        using var external = new BackendProcessManager("http://127.0.0.1:48923",
            NullLogger<BackendProcessManager>.Instance, scope.Frontend, scope.Executable,
            () => true, _ => Task.FromResult(true), _ => throw new AssertionException("must not launch"));
        await external.EnsureStartedAsync().ConfigureAwait(false);
        var active = Path.Combine(scope.BackendPaths.ConfigDirectory, ".env");
        var rejected = false;
        try
        {
            await BundledProviderSetup.ApplyAsync(scope.Frontend, external, null,
                new ClientActionDispatcher(), new("local", "Local", false, false), "model", null, null).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { rejected = true; }
        rejected.Should().BeTrue();
        File.Exists(active).Should().BeFalse();
        external.IsExternal.Should().BeTrue();
    }

    [Test]
    public async Task Retargeted_runtime_does_not_gain_write_authority_from_a_still_running_owned_processAsync()
    {
        using var scope = new SetupScope();
        await scope.Backend.EnsureStartedAsync().ConfigureAwait(false);
        scope.Backend.OwnsCurrentTarget.Should().BeTrue();
        scope.Backend.UpdateApiUrl("https://external.example");
        scope.Backend.IsRunning.Should().BeTrue();
        scope.Backend.OwnsCurrentTarget.Should().BeFalse();
        Func<Task> save = () => BundledProviderSetup.ApplyAsync(scope.Frontend, scope.Backend, null,
            new ClientActionDispatcher(), new("local", "Local", false, false), "model", null, null);
        await save.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        File.Exists(Path.Combine(scope.BackendPaths.ConfigDirectory, ".env")).Should().BeFalse();
        scope.Backend.IsRunning.Should().BeTrue();
    }

    private sealed class SetupScope : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "sharpclaw-provider-setup-" + Guid.NewGuid().ToString("N"));
        public SetupScope()
        {
            Frontend = new FrontendInstanceService(Path.Combine(root, "frontend"), root, root);
            BackendPaths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, Frontend.BundledBackendInstanceRoot, root);
            BackendPaths.EnsureDirectories();
            File.WriteAllText(Path.Combine(BackendPaths.ConfigDirectory, ".env.template"), "Unrelated__Option=preserved\n");
            Executable = Path.Combine(root, "runtime");
            File.WriteAllText(Executable, string.Empty);
            Backend = new BackendProcessManager("http://127.0.0.1:48923", NullLogger<BackendProcessManager>.Instance,
                Frontend, Executable, () => false, _ => Task.FromResult(false), _ => { });
        }
        public FrontendInstanceService Frontend { get; }
        public SharpClawInstancePaths BackendPaths { get; }
        public string Executable { get; }
        public BackendProcessManager Backend { get; }
        public void Dispose() { Backend.Dispose(); Directory.Delete(root, recursive: true); }
    }
}
