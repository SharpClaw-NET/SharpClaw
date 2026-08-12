using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Gateway.Infrastructure;

public sealed class GatewayActionMiddleware(
    RequestDelegate next,
    GatewayBackgroundActionBoundary actions,
    ILogger<GatewayActionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var invocation = CreateInvocation(context, "receive");
        var executionContext = CreateExecutionContext(context);
        try
        {
            await RunActionAsync(
                context,
                new SharpClawActionKey("gateway.request.receive"),
                invocation,
                (_, cancellationToken) => RunAuthenticatedRequestAsync(
                    context,
                    invocation,
                    cancellationToken,
                    executionContext),
                context.RequestAborted,
                executionContext);
        }
        catch (KernelActionCancelledException exception)
        {
            await SignalAsync(
                actions,
                new SharpClawActionKey("gateway.request.cancel"),
                invocation with { Operation = "cancel" },
                exception,
                executionContext);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await SignalAsync(
                actions,
                new SharpClawActionKey("gateway.request.cancel"),
                invocation with { Operation = "cancel" },
                exception,
                executionContext);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Gateway action boundary failed for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
            await SignalAsync(
                actions,
                new SharpClawActionKey("gateway.request.fail"),
                invocation with { Operation = "fail" },
                exception,
                executionContext);
            throw;
        }
    }

    private async ValueTask<bool> RunAuthenticatedRequestAsync(
        HttpContext context,
        GatewayActionInvocation invocation,
        CancellationToken cancellationToken,
        KernelActionExecutionContext executionContext)
    {
        RequireAllowed(await RunActionAsync(
            context,
            new SharpClawActionKey("gateway.request.authenticate"),
            invocation with { Operation = "authenticate" },
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext),
            "gateway.request.authenticate");
        RequireAllowed(await RunActionAsync(
            context,
            new SharpClawActionKey("gateway.request.authorize"),
            invocation with { Operation = "authorize" },
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext),
            "gateway.request.authorize");
        RequireAllowed(await RunActionAsync(
            context,
            new SharpClawActionKey("gateway.request.route"),
            invocation with { Operation = "route" },
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext),
            "gateway.request.route");

        var isStream = invocation.IsStream;
        if (isStream)
        {
            try
            {
                await RunActionAsync(
                    context,
                    new SharpClawActionKey("gateway.stream.open"),
                    invocation with { Operation = "stream.open" },
                    (_, ct) => RunForwardAsync(context, invocation, ct, executionContext),
                    cancellationToken,
                    executionContext);
                await RunActionAsync(
                    context,
                    new SharpClawActionKey("gateway.stream.close"),
                    invocation with { Operation = "stream.close" },
                    static (_, _) => ValueTask.FromResult(true),
                    cancellationToken,
                    executionContext);
            }
            catch (KernelActionCancelledException exception)
            {
                await SignalAsync(
                    actions,
                    new SharpClawActionKey("gateway.stream.cancel"),
                    invocation with { Operation = "stream.cancel" },
                    exception,
                    executionContext);
                throw;
            }
            catch (OperationCanceledException exception)
            {
                await SignalAsync(
                    actions,
                    new SharpClawActionKey("gateway.stream.cancel"),
                    invocation with { Operation = "stream.cancel" },
                    exception,
                    executionContext);
                throw;
            }
            catch (Exception exception)
            {
                await SignalAsync(
                    actions,
                    new SharpClawActionKey("gateway.stream.fail"),
                    invocation with { Operation = "stream.fail" },
                    exception,
                    executionContext);
                throw;
            }
        }
        else
        {
            await RunForwardAsync(context, invocation, cancellationToken, executionContext);
        }

            await RunActionAsync(
                context,
                new SharpClawActionKey("gateway.request.response"),
            invocation with { Operation = "response" },
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext);
        return true;
    }

    private async ValueTask<bool> RunForwardAsync(
        HttpContext context,
        GatewayActionInvocation invocation,
        CancellationToken cancellationToken,
        KernelActionExecutionContext executionContext)
    {
        var actionKey = ResolveForwardAction(context);
        if (!invocation.IsStream)
        {
            await RunActionAsync(
                context,
                actionKey,
                invocation with { Operation = actionKey.Value },
                async (_, ct) =>
                {
                    await next(context);
                    return true;
                },
                cancellationToken,
                executionContext);
            return true;
        }

        var originalBody = context.Response.Body;
        await using var actionBody = new GatewayActionResponseStream(
            originalBody,
            actions,
            invocation,
            logger,
            executionContext);
        context.Response.Body = actionBody;
        try
        {
            await RunActionAsync(
                context,
                actionKey,
                invocation with { Operation = actionKey.Value },
                async (_, ct) =>
                {
                    await next(context);
                    return true;
                },
                cancellationToken,
                executionContext);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        return true;
    }

    private static SharpClawActionKey ResolveForwardAction(HttpContext context)
    {
        if (context.Items.ContainsKey(RemoteRuntimeBridgeHost.BridgeAppItemKey))
            return new SharpClawActionKey("gateway.bridge.forward");

        if (context.GetEndpoint()?.Metadata.GetMetadata<RemoteRuntimeBridgeCredentialMetadata>()
            is not null)
        {
            return new SharpClawActionKey("gateway.bridge.forward");
        }

        if (context.Request.Path.StartsWithSegments("/api/modules"))
            return new SharpClawActionKey("gateway.module.endpoint.dispatch");

        return new SharpClawActionKey("gateway.request.forward");
    }

    private ValueTask<TResult> RunActionAsync<TPayload, TResult>(
        HttpContext context,
        SharpClawActionKey actionKey,
        TPayload payload,
        Func<TPayload, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken,
        KernelActionExecutionContext executionContext) =>
        actions.RunActionAsync(
            actionKey,
            payload,
            terminal,
            cancellationToken,
            executionContext);

    private static GatewayActionInvocation CreateInvocation(
        HttpContext context,
        string operation) =>
        new(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            operation,
            IsStreamRequest(context),
            PairId: context.Items.TryGetValue(
                RemoteRuntimeBridgeHost.ActivePairItemKey,
                out var pair) && pair is RemoteRuntimePairingRegistrySnapshot snapshot
                ? snapshot.PairId
                : null);

    private static bool IsStreamRequest(HttpContext context) =>
        context.Request.Path.Value?.Contains("/stream", StringComparison.OrdinalIgnoreCase) == true
        || context.Request.Headers.Accept.Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);

    private static KernelActionExecutionContext CreateExecutionContext(
        HttpContext context)
    {
        var traceValue = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var traceId = CreateIdentity(traceValue, "trace");
        var idempotencyValue = context.Request.Headers["Idempotency-Key"].ToString();
        var idempotencyKey = string.IsNullOrWhiteSpace(idempotencyValue)
            ? traceId
            : CreateIdentity(idempotencyValue, "idempotency");
        var features = context.Items.TryGetValue(typeof(ExtensionFeatureSet), out var item)
            && item is ExtensionFeatureSet activeFeatures
            ? activeFeatures
            : ExtensionFeatureSet.Empty;

        return new KernelActionExecutionContext(
            CreatePrincipal(context),
            features,
            traceId,
            idempotencyKey);
    }

    private static RequestPrincipal CreatePrincipal(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
            return RequestPrincipal.Anonymous;

        var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.Identity.Name;
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException(
                "The authenticated Gateway principal has no stable subject identifier.");

        var displayName = context.User.FindFirst(ClaimTypes.Name)?.Value
            ?? context.User.Identity.Name;
        var roles = context.User.Claims
            .Where(static claim => claim.Type == ClaimTypes.Role || claim.Type == "role")
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        return new RequestPrincipal(subject, displayName, roles, true);
    }

    private static Guid CreateIdentity(string value, string purpose)
    {
        if (Guid.TryParse(value, out var identity) && identity != Guid.Empty)
            return identity;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SharpClaw:gateway:{purpose}:{value}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static void RequireAllowed(bool allowed, string actionKey)
    {
        if (!allowed)
        {
            throw new KernelActionExecutionException(
                $"Gateway action '{actionKey}' denied the request.");
        }
    }

    private static async ValueTask SignalAsync(
        GatewayBackgroundActionBoundary actions,
        SharpClawActionKey actionKey,
        GatewayActionInvocation invocation,
        Exception original,
        KernelActionExecutionContext executionContext)
    {
        try
        {
            await actions.RunActionAsync(
                actionKey,
                invocation,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None,
                executionContext);
        }
        catch (Exception signalFailure)
        {
            throw new AggregateException(original, signalFailure);
        }
    }
}

internal sealed class GatewayActionResponseStream(
    Stream inner,
    GatewayBackgroundActionBoundary actions,
    GatewayActionInvocation baseInvocation,
    ILogger logger,
    KernelActionExecutionContext executionContext) : Stream
{
    private int _chunk;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var invocation = baseInvocation with
        {
            Operation = "stream.chunk",
            ByteCount = buffer.Length,
        };
        await actions.RunActionAsync(
            new SharpClawActionKey("gateway.stream.chunk.receive"),
            invocation,
            static (_, _) => ValueTask.FromResult(true),
            cancellationToken,
            executionContext);
        await actions.RunActionAsync(
            new SharpClawActionKey("gateway.stream.chunk.forward"),
            invocation with { Operation = $"stream.chunk.forward:{Interlocked.Increment(ref _chunk)}" },
            async (_, ct) =>
            {
                await inner.WriteAsync(buffer, ct);
                return true;
            },
            cancellationToken,
            executionContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            logger.LogTrace("Gateway stream response wrapper disposed after {ChunkCount} chunks.", _chunk);
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
