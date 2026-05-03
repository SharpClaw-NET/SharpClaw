using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SharpClaw.Application.Core.Services.Triggers;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.Http;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Tests for Phase 11: <see cref="WebhookTriggerSource"/> route registration,
/// HMAC-SHA256 signature validation, and request handling.
/// </summary>
[TestFixture]
public class WebhookTriggerSourceTests
{
    private WebhookTriggerSource _source = null!;
    private SpyWebhookRouteRegistrar _registrar = null!;

    [SetUp]
    public void SetUp()
    {
        _source = new WebhookTriggerSource(NullLogger<WebhookTriggerSource>.Instance);
        _registrar = new SpyWebhookRouteRegistrar();
        _source.SetRouteRegistrar(_registrar);
    }

    // ── TriggerKey ────────────────────────────────────────────────

    [Test]
    public void TriggerKey_IsWebhook()
    {
        _source.TriggerKey.Should().Be(HttpTriggerKeys.Webhook);
    }

    // ── StartAsync ────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_WithValidRoute_RegistersRouteWithRegistrar()
    {
        var ctx = MakeContext("/my-hook");
        await _source.StartAsync([ctx], CancellationToken.None);

        _registrar.RegisteredRoutes.Should().ContainSingle()
            .Which.Should().Be("/webhooks/tasks/my-hook");
    }

    [Test]
    public async Task StartAsync_WithAbsolutePath_UsesRouteAsIs()
    {
        var ctx = MakeContext("/webhooks/tasks/custom-path");
        await _source.StartAsync([ctx], CancellationToken.None);

        _registrar.RegisteredRoutes.Should().ContainSingle()
            .Which.Should().Be("/webhooks/tasks/custom-path");
    }

    [Test]
    public async Task StartAsync_WithEmptyRoute_DoesNotRegisterRoute()
    {
        var ctx = MakeContext("");
        await _source.StartAsync([ctx], CancellationToken.None);

        _registrar.RegisteredRoutes.Should().BeEmpty();
    }

    [Test]
    public async Task StartAsync_WithMultipleBindings_RegistersAllRoutes()
    {
        var ctxA = MakeContext("/hook-a");
        var ctxB = MakeContext("/hook-b");
        await _source.StartAsync([ctxA, ctxB], CancellationToken.None);

        _registrar.RegisteredRoutes.Should().HaveCount(2);
    }

    // ── StopAsync ─────────────────────────────────────────────────

    [Test]
    public async Task StopAsync_ClearsActiveRoutes()
    {
        var ctx = MakeContext("/stop-test");
        await _source.StartAsync([ctx], CancellationToken.None);
        await _source.StopAsync();

        // After stop, incoming requests to the previously-active route return 404
        var status = await _source.HandleRequestAsync(
            "/webhooks/tasks/stop-test", "body", "{}", CancellationToken.None);
        status.Should().Be(404);
    }

    // ── HandleRequestAsync — 404 when route inactive ──────────────

    [Test]
    public async Task HandleRequest_WhenRouteNotActive_Returns404()
    {
        var status = await _source.HandleRequestAsync(
            "/webhooks/tasks/unknown", "body", "{}", CancellationToken.None);
        status.Should().Be(404);
    }

    // ── HandleRequestAsync — 202 without HMAC ────────────────────

    [Test]
    public async Task HandleRequest_WithNoSecret_Returns202()
    {
        var ctx = MakeContext("/open-hook");
        await _source.StartAsync([ctx], CancellationToken.None);

        var status = await _source.HandleRequestAsync(
            "/webhooks/tasks/open-hook", "payload", "{}", CancellationToken.None);
        status.Should().Be(202);
    }

    [Test]
    public async Task HandleRequest_WithNoSecret_FiresContext()
    {
        var spy = new SpyTriggerSourceContext("/fire-hook");
        await _source.StartAsync([spy], CancellationToken.None);

        await _source.HandleRequestAsync(
            "/webhooks/tasks/fire-hook", "data", "{}", CancellationToken.None);

        spy.FireCount.Should().Be(1);
    }

    [Test]
    public async Task HandleRequest_PassesBodyAndHeadersAsParameters()
    {
        var spy = new SpyTriggerSourceContext("/params-hook");
        await _source.StartAsync([spy], CancellationToken.None);

        var headers = JsonSerializer.Serialize(new { ContentType = "application/json" });
        await _source.HandleRequestAsync(
            "/webhooks/tasks/params-hook", "my-body", headers, CancellationToken.None);

        spy.LastParameters.Should().ContainKey("WebhookBody")
            .WhoseValue.Should().Be("my-body");
        spy.LastParameters.Should().ContainKey("WebhookHeaders");
    }

    // ── HandleRequestAsync — HMAC validation ─────────────────────

    [Test]
    public async Task HandleRequest_WithValidHmacSignature_Returns202()
    {
        const string secret = "test-secret";
        const string body   = "hello world";
        var sig = ComputeHmac(secret, body);
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = "sha256=" + sig,
        });

        var ctx = MakeContext("/secure-hook", secretEnvVar: "WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("WEBHOOK_SECRET", secret);

        try
        {
            await _source.StartAsync([ctx], CancellationToken.None);
            var status = await _source.HandleRequestAsync(
                "/webhooks/tasks/secure-hook", body, headers, CancellationToken.None);
            status.Should().Be(202);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_SECRET", null);
        }
    }

    [Test]
    public async Task HandleRequest_WithInvalidHmacSignature_Returns401()
    {
        const string secret = "test-secret";
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = "sha256=badhash",
        });

        var ctx = MakeContext("/secure-hook-bad", secretEnvVar: "WEBHOOK_BAD_SECRET");
        Environment.SetEnvironmentVariable("WEBHOOK_BAD_SECRET", secret);

        try
        {
            await _source.StartAsync([ctx], CancellationToken.None);
            var status = await _source.HandleRequestAsync(
                "/webhooks/tasks/secure-hook-bad", "payload", headers, CancellationToken.None);
            status.Should().Be(401);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_BAD_SECRET", null);
        }
    }

    [Test]
    public async Task HandleRequest_WhenSecretEnvVarNotSet_Returns401()
    {
        var ctx = MakeContext("/missing-secret", secretEnvVar: "WEBHOOK_MISSING_ENV_VAR_XYZ");
        Environment.SetEnvironmentVariable("WEBHOOK_MISSING_ENV_VAR_XYZ", null);

        await _source.StartAsync([ctx], CancellationToken.None);
        var status = await _source.HandleRequestAsync(
            "/webhooks/tasks/missing-secret", "body", "{}", CancellationToken.None);
        status.Should().Be(401);
    }

    [Test]
    public async Task HandleRequest_WithMissingSignatureHeader_Returns401()
    {
        const string secret = "test-secret";
        var headers = JsonSerializer.Serialize(new { OtherHeader = "value" }); // no sig header

        var ctx = MakeContext("/no-sig-header", secretEnvVar: "WEBHOOK_NO_SIG_SECRET");
        Environment.SetEnvironmentVariable("WEBHOOK_NO_SIG_SECRET", secret);

        try
        {
            await _source.StartAsync([ctx], CancellationToken.None);
            var status = await _source.HandleRequestAsync(
                "/webhooks/tasks/no-sig-header", "body", headers, CancellationToken.None);
            status.Should().Be(401);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_NO_SIG_SECRET", null);
        }
    }

    [Test]
    public async Task HandleRequest_WithCustomSignatureHeader_UsesIt()
    {
        const string secret = "test-secret";
        const string body   = "payload";
        const string customHeader = "X-Custom-Sig";
        var sig = ComputeHmac(secret, body);
        var headers = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [customHeader] = "sha256=" + sig,
        });

        var ctx = MakeContext("/custom-header-hook",
            secretEnvVar: "WEBHOOK_CUSTOM_SIG_SECRET",
            signatureHeader: customHeader);
        Environment.SetEnvironmentVariable("WEBHOOK_CUSTOM_SIG_SECRET", secret);

        try
        {
            await _source.StartAsync([ctx], CancellationToken.None);
            var status = await _source.HandleRequestAsync(
                "/webhooks/tasks/custom-header-hook", body, headers, CancellationToken.None);
            status.Should().Be(202);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_CUSTOM_SIG_SECRET", null);
        }
    }

    // ── Reload / re-register behaviour ───────────────────────────

    [Test]
    public async Task StartAsync_CalledTwiceForSameRoute_RegistersOnce()
    {
        var ctx = MakeContext("/reload-hook");
        await _source.StartAsync([ctx], CancellationToken.None);
        await _source.StopAsync();
        await _source.StartAsync([ctx], CancellationToken.None); // second load

        _registrar.RegisteredRoutes.Should().ContainSingle(); // EnsureRegistered called twice but only first matters
    }

    // ── SetRouteRegistrar ─────────────────────────────────────────

    [Test]
    public async Task StartAsync_WithNoRegistrarSet_DoesNotThrow()
    {
        var source = new WebhookTriggerSource(NullLogger<WebhookTriggerSource>.Instance);
        // no SetRouteRegistrar call

        var ctx = MakeContext("/no-registrar");
        var act = async () => await source.StartAsync([ctx], CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static StubTriggerSourceContext MakeContext(
        string route,
        string? secretEnvVar = null,
        string? signatureHeader = null)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HttpTriggerKeys.WebhookRoute] = route,
        };
        if (secretEnvVar is not null)
            parameters[HttpTriggerKeys.WebhookSecretEnvVar] = secretEnvVar;
        if (signatureHeader is not null)
            parameters[HttpTriggerKeys.WebhookSignatureHeader] = signatureHeader;

        var def = new TaskTriggerDefinition
        {
            TriggerKey = HttpTriggerKeys.Webhook,
            Parameters = parameters,
        };
        return new StubTriggerSourceContext(def);
    }

    private static string ComputeHmac(string secret, string body)
    {
        var key  = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    // ── Test doubles ─────────────────────────────────────────────

    private sealed class SpyWebhookRouteRegistrar : IWebhookRouteRegistrar
    {
        private readonly List<string> _registered = [];
        public IReadOnlyList<string> RegisteredRoutes => _registered;

        public void EnsureRegistered(string routePath)
        {
            if (!_registered.Contains(routePath, StringComparer.OrdinalIgnoreCase))
                _registered.Add(routePath);
        }
    }

    private sealed class StubTriggerSourceContext(
        TaskTriggerDefinition definition,
        Guid taskDefinitionId = default) : ITaskTriggerSourceContext
    {
        public TaskTriggerDefinition Definition { get; } = definition;
        public Guid TaskDefinitionId { get; } = taskDefinitionId;
        public int FireCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastParameters { get; private set; }

        public Task FireAsync(
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            FireCount++;
            LastParameters = parameters;
            return Task.CompletedTask;
        }
    }

    private sealed class SpyTriggerSourceContext(string route) : ITaskTriggerSourceContext
    {
        public TaskTriggerDefinition Definition { get; } = new TaskTriggerDefinition
        {
            TriggerKey = HttpTriggerKeys.Webhook,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [HttpTriggerKeys.WebhookRoute] = route,
            },
        };
        public Guid TaskDefinitionId { get; } = Guid.NewGuid();
        public int FireCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastParameters { get; private set; }

        public Task FireAsync(
            IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            FireCount++;
            LastParameters = parameters;
            return Task.CompletedTask;
        }
    }
}
