using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Tests.Gateway;

[TestFixture]
public sealed class GatewayActionBoundaryTests
{
    [Test]
    public void Manifest_matches_the_published_gateway_action_inventory()
    {
        GatewayActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(
                "gateway.request.receive",
                "gateway.request.authenticate",
                "gateway.request.authorize",
                "gateway.request.route",
                "gateway.request.forward",
                "gateway.request.response",
                "gateway.request.fail",
                "gateway.request.cancel",
                "gateway.stream.open",
                "gateway.stream.chunk.receive",
                "gateway.stream.chunk.forward",
                "gateway.stream.close",
                "gateway.stream.fail",
                "gateway.stream.cancel",
                "gateway.endpoint.dispatch",
                "gateway.bridge.session.validate",
                "gateway.bridge.forward");
    }

    [Test]
    public async Task Buffered_request_uses_one_root_context_for_all_gateway_actions()
    {
        var probe = new GatewayProbe();
        var boundary = CreateBoundary(probe);
        var nextCalls = 0;
        var middleware = CreateMiddleware(boundary, async context =>
        {
            Interlocked.Increment(ref nextCalls);
            await context.Response.WriteAsync("ok", context.RequestAborted);
        });
        var context = CreateContext("GET", "/api/test", "user-a", "request-a");

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(1);
        ReadBody(context).Should().Be("ok");
        probe.ActionKeys.Should().Equal(
            "gateway.request.receive",
            "gateway.request.authenticate",
            "gateway.request.authorize",
            "gateway.request.route",
            "gateway.endpoint.dispatch",
            "gateway.request.response");
        probe.RequestContexts.Should().OnlyContain(value => value.SubjectId == "user-a");
        probe.RequestContexts.Select(value => value.TraceId).Distinct().Should().ContainSingle();
        probe.RequestContexts.Select(value => value.IdempotencyKey).Distinct().Should().ContainSingle();
    }

    [Test]
    public async Task Concurrent_requests_keep_principal_and_context_authority_isolated()
    {
        var probe = new GatewayProbe();
        var boundary = CreateBoundary(probe);
        var middleware = CreateMiddleware(boundary, context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var first = CreateContext("GET", "/api/one", "user-a", "request-a");
        var second = CreateContext("GET", "/api/two", "user-b", "request-b");

        await Task.WhenAll(middleware.InvokeAsync(first), middleware.InvokeAsync(second));

        probe.RequestContexts.Should().HaveCount(10);
        probe.RequestContexts.Select(value => value.SubjectId).Distinct()
            .Should().BeEquivalentTo(["user-a", "user-b"]);
        probe.RequestContexts.Select(value => value.TraceId).Distinct().Should().HaveCount(2);
        probe.RequestContexts.Select(value => value.IdempotencyKey).Distinct().Should().HaveCount(2);
    }

    [Test]
    public async Task Replace_result_without_forward_terminal_fails_closed()
    {
        var probe = new GatewayProbe { ReplaceAction = "gateway.request.forward" };
        var boundary = CreateBoundary(probe);
        var nextCalls = 0;
        var middleware = CreateMiddleware(boundary, _ =>
        {
            Interlocked.Increment(ref nextCalls);
            return Task.CompletedTask;
        });

        var action = () => middleware.InvokeAsync(
            CreateContext("GET", "/api/chat", "user-a", "request-a"));

        await action.Should().ThrowAsync<KernelActionFailedException>();
        nextCalls.Should().Be(0);
        probe.ActionKeys.Should().Contain("gateway.request.fail");
    }

    [Test]
    public async Task Action_cancellation_stops_forwarding_and_dispatches_cancel()
    {
        var probe = new GatewayProbe { CancelAction = "gateway.request.forward" };
        var boundary = CreateBoundary(probe);
        var nextCalls = 0;
        var middleware = CreateMiddleware(boundary, _ =>
        {
            Interlocked.Increment(ref nextCalls);
            return Task.CompletedTask;
        });

        var action = () => middleware.InvokeAsync(
            CreateContext("GET", "/api/chat", "user-a", "request-a"));

        await action.Should().ThrowAsync<KernelActionCancelledException>();
        nextCalls.Should().Be(0);
        probe.ActionKeys.Should().Contain("gateway.request.cancel");
    }

    [Test]
    public async Task Authorization_replacement_cannot_grant_a_denied_request()
    {
        var probe = new GatewayProbe { RestrictAction = "gateway.request.authorize" };
        var boundary = CreateBoundary(probe);
        var nextCalls = 0;
        var middleware = CreateMiddleware(boundary, _ =>
        {
            Interlocked.Increment(ref nextCalls);
            return Task.CompletedTask;
        });

        var action = () => middleware.InvokeAsync(
            CreateContext("GET", "/api/test", "user-a", "request-a"));

        await action.Should().ThrowAsync<KernelActionFailedException>();
        nextCalls.Should().Be(0);
        probe.ActionKeys.Should().Contain("gateway.request.fail");
    }

    [Test]
    public async Task Stream_chunks_are_dispatched_with_cancellation_and_failure_visibility()
    {
        var probe = new GatewayProbe();
        var boundary = CreateBoundary(probe);
        var middleware = CreateMiddleware(boundary, async context =>
        {
            await context.Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes("first"),
                context.RequestAborted);
            throw new InvalidOperationException("stream failure");
        });
        var context = CreateContext(
            "GET",
            "/api/test/stream",
            "user-a",
            "request-a",
            "text/event-stream");

        await middleware.Invoking(value => value.InvokeAsync(context))
            .Should().ThrowAsync<KernelActionFailedException>();

        ReadBody(context).Should().Be("first");
        probe.ActionKeys.Should().ContainInOrder(
            "gateway.stream.open",
            "gateway.stream.chunk.receive",
            "gateway.stream.chunk.forward",
            "gateway.stream.fail",
            "gateway.request.fail");
        probe.ActionKeys.Should().NotContain("gateway.stream.close");
    }

    [Test]
    public void Source_inventory_routes_gateway_products_through_the_action_middleware()
    {
        var sourceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT")
            ?? Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                ".."));
        var program = File.ReadAllText(Path.Combine(sourceRoot, "SharpClaw.Gateway", "Program.cs"));
        var proxy = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Gateway",
            "GatewayProxyEndpoints.cs"));

        program.Should().Contain("UseMiddleware<GatewayActionMiddleware>()");
        program.Should().Contain("MapGatewayProxyEndpoints()");
        proxy.Should().Contain("/api/{**path}");
    }

    private static GatewayActionMiddleware CreateMiddleware(
        GatewayBackgroundActionBoundary boundary,
        RequestDelegate next) =>
        new(next, boundary, NullLogger<GatewayActionMiddleware>.Instance);

    private static DefaultHttpContext CreateContext(
        string method,
        string path,
        string subject,
        string idempotencyKey,
        string? accept = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        if (accept is not null)
            context.Request.Headers.Accept = accept;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)],
            "test"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static GatewayBackgroundActionBoundary CreateBoundary(GatewayProbe probe)
    {
        var grants = GatewayActionManifest.Required.ToDictionary(
            static key => key.Value,
            static key => ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap,
            StringComparer.Ordinal);
        grants["gateway.request.forward"] |=
            ActionInterceptionCapabilities.ReplaceResult |
            ActionInterceptionCapabilities.Cancel;
        grants["gateway.request.authorize"] |=
            ActionInterceptionCapabilities.ReplaceResult;
        var graph = TestServiceGraph.Compile(
            [new GatewayProbeRegistration(probe)],
            new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
                StringComparer.Ordinal)
            {
                ["gateway-boundary-test"] = grants,
            },
            SensitiveActionApprovals = GatewayActionManifest.Required
                .Where(key => KernelActionCatalog.DescriptorFor(key).ContainsSensitiveData)
                .Select(key =>
                {
                    var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
                    var types = KernelSchemaIdentity.ActionTypes(
                        descriptor,
                        typeof(KernelActionEnvelope),
                        typeof(object));
                    return new KernelSensitiveActionApproval(
                        "gateway-boundary-test",
                        key,
                        descriptor.Version,
                        types.ActionType.AssemblyQualifiedName!,
                        types.ResultType.AssemblyQualifiedName!,
                        KernelSchemaIdentity.Action(
                            descriptor,
                            typeof(KernelActionEnvelope),
                            typeof(object)));
                })
                .ToArray(),
        });
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()));
        return new GatewayBackgroundActionBoundary(graph, dispatcher);
    }

    private sealed class GatewayProbe
    {
        public ConcurrentQueue<string> ActionKeys { get; } = new();
        public ConcurrentQueue<(string SubjectId, Guid TraceId, Guid IdempotencyKey)> RequestContexts { get; } = new();
        public string? ReplaceAction { get; init; }
        public string? CancelAction { get; init; }
        public string? RestrictAction { get; init; }
    }

    private sealed class GatewayProbeRegistration(GatewayProbe probe) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "gateway-boundary-test",
            "Gateway boundary test",
            "gateway-boundary");

        public void ConfigureServices(IServiceCollection extension)
        {
            extension.AddSingleton(probe);
            extension.AddSingleton<GatewayProbeInterceptor>();
            foreach (var key in GatewayActionManifest.Required)
            {
                extension.OnAction(key).Use<GatewayProbeInterceptor>(new HookOrdering(
                    $"gateway-boundary-{key.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }
    }

    private sealed class GatewayProbeInterceptor(GatewayProbe probe)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.ActionKeys.Enqueue(context.ActionKey.Value);
            if (context.ActionKey.Value.StartsWith("gateway.request.", StringComparison.Ordinal)
                || context.ActionKey.Value.StartsWith("gateway.stream.", StringComparison.Ordinal))
            {
                probe.RequestContexts.Enqueue((
                    context.Caller.SubjectId,
                    context.TraceId,
                    context.IdempotencyKey));
            }

            if (string.Equals(probe.ReplaceAction, context.ActionKey.Value, StringComparison.Ordinal))
                return ValueTask.FromResult(control.ReplaceResult(true, "test replacement"));
            if (string.Equals(probe.CancelAction, context.ActionKey.Value, StringComparison.Ordinal))
                return ValueTask.FromResult(control.Cancel("TEST_CANCELLED", "test cancellation"));
            if (string.Equals(probe.RestrictAction, context.ActionKey.Value, StringComparison.Ordinal))
                return ValueTask.FromResult(control.ReplaceResult(false, "test restriction"));

            return control.ProceedAsync(cancellationToken);
        }
    }
}
