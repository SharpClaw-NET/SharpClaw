using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class ModularFrontendTests
{
    [Test]
    public void MissingUnknownDuplicateAndIncompleteProviderSelectionFailClosed()
    {
        var provider = new SharpClawProviderSetupOption("arbitrary", "Third party", false, false);
        StatelessChatReadiness.CanChat(null).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, "arbitrary", "model", [])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, "unknown", "model", [provider])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, "arbitrary", " ", [provider])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, null, null, [provider])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(true, "arbitrary", "model", [provider])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, "arbitrary", "model", [provider, provider])).Should().BeFalse();
        StatelessChatReadiness.CanChat(new(false, "ARBITRARY", "model", [provider])).Should().BeTrue();
        var configured = new SharpClawProviderSetup(false, "arbitrary", "model", [provider]);
        StatelessChatReadiness.CanChat(configured, new("arbitrary", ["other"])).Should().BeFalse();
        StatelessChatReadiness.CanChat(configured, new("different-provider", ["model"])).Should().BeFalse();
        StatelessChatReadiness.CanChat(configured, new("arbitrary", ["model"])).Should().BeTrue();
    }

    [Test]
    public async Task ChatReadinessUsesOnlyMetadataAndRejectsDiscoveryFailureAsync()
    {
        var requests = new List<string>();
        var failCatalog = false;
        using var handler = new Handler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (string.Equals(path, "/setup/provider", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new StringContent("{\"setupRequired\":false,\"providerKey\":\"arbitrary\",\"model\":\"model\", \"providers\":[\n  {\"key\":\"arbitrary\",\"displayName\":\"Third party\",\"requiresApiKey\":false,\"requiresEndpoint\":false}]}") };
            path.Should().Be("/setup/models");
            return failCatalog ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
            { Content = new StringContent("""{"providerKey":"arbitrary","models":["model"]}""") };
        });
        using var http = new HttpClient(handler, disposeHandler: false)
        { BaseAddress = new Uri("https://runtime.example") };
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance,
          new ClientActionDispatcher(), fixedApiKey: "test-key");
        await using var apiAsyncDisposal = api.ConfigureAwait(false);
        (await StatelessChatReadiness.CheckAsync(api).ConfigureAwait(false)).Should().BeTrue();
        failCatalog = true;
        (await StatelessChatReadiness.CheckAsync(api).ConfigureAwait(false)).Should().BeFalse();
        requests.Should().Equal("/setup/provider", "/setup/models", "/setup/provider", "/setup/models");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        Func<Task> canceled = async () => await StatelessChatReadiness.CheckAsync(api, cancellation.Token).ConfigureAwait(false);
        await canceled.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
    }

    [Test]
    public void ManifestSettingsAreDataWithNoBuiltInModuleNames()
    {
        const string settingsManifest =
            """{"frontend":{"schemaVersion":1,"settings":[""" + "\n"
            + """{"id":"preferences","title":"Preferences","readPath":"/fixture/settings","savePath":"/fixture/settings"}]}}""";
        var pages = SharpClawModuleSettings.ReadManifest(settingsManifest, "unknown_module", "Unknown module");
        pages.Single().Should().Be(new SharpClawModuleSettingsPage(
            "unknown_module", "Unknown module", "preferences", "Preferences", "/fixture/settings", "/fixture/settings"));
        SharpClawModuleSettings.ReadManifest("{}", "empty", "Empty").Should().BeEmpty();
        const string existingMetadata =
            """{ /* existing package metadata remains compatible */""" + "\n"
            + """  "vendor":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":true}}}}}}}}}}""";
        SharpClawModuleSettings.ReadManifest(existingMetadata, "empty", "Empty").Should().BeEmpty();
    }

    [TestCase("//attacker.example/settings")]
    [TestCase("https://attacker.example/settings")]
    [TestCase("/fixture/settings?target=evil")]
    [TestCase("/fixture/../env/core")]
    [TestCase("/fixture/%2fsettings")]
    [TestCase("/fixture/{id}")]
    public void ModuleSettingsCannotDeclareAuthorityOrTemplatedTargets(string path)
    {
        var json = JsonSerializer.Serialize(new
        {
            frontend = new
            {
                schemaVersion = 1,
                settings = new[] { new { id = "test", title = "Test", readPath = path, savePath = "/fixture/settings" } }
            }
        });
        Assert.Throws<InvalidDataException>(() => SharpClawModuleSettings.ReadManifest(json, "module", "Module"));
    }

    [Test]
    public void GenericSettingsSupportTextBooleanChoiceAndOpaqueWriteOnlySecrets()
    {
        const string settingsDocument =
            """{"schemaVersion":1,"fields":[{"key":"name","label":"Name","kind":"text","required":true},""" + "\n"
            + """{"key":"enabled","label":"Enabled","kind":"boolean"},""" + "\n"
            + """{"key":"mode","label":"Mode","kind":"choice","choices":["one","two"]},""" + "\n"
            + """{"key":"token","label":"Token","kind":"secret"}],""" + "\n"
            + """  "values":{"name":"example","enabled":true,"mode":"two"}}""";
        var doc = SharpClawModuleSettings.ReadDocument(settingsDocument);
        doc.Fields.Should().HaveCount(4);
        doc.Values.Should().NotContainKey("token");
    }

    [TestCase("{\"schemaVersion\":2,\"fields\":[],\"values\":{}}")]
    [TestCase("{\"schemaVersion\":1,\"fields\":[{\"key\":\"x\",\"label\":\"X\",\"kind\":\"xaml\"}],\"values\":{}}")]
    [TestCase("{\"schemaVersion\":1,\"fields\":[{\"key\":\"x\",\"label\":\"X\",\"kind\":\"boolean\"}],\"values\":{\"x\":\"true\"}}")]
    [TestCase("{\"schemaVersion\":1,\"fields\":[],\"values\":{\"undeclared\":true}}")]
    public void InvalidSettingsSchemasAndValuesAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => SharpClawModuleSettings.ReadDocument(json));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
