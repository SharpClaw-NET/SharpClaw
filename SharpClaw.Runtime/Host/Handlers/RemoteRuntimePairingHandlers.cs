using Microsoft.AspNetCore.Http;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup(RemoteRuntimeBridgePaths.RegistryPrefix)]
public static class RemoteRuntimePairingHandlers
{
    [MapPost("/invitation")]
    public static async Task<IResult> CreateInvitation(
        HttpContext context,
        RemoteRuntimeRegistryInvitationRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
    {
        return await runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("create-invitation", RemoteRuntimeBridgePaths.RegistryPrefix),
            async (_, ct) =>
            {
                try
                {
                    byte[]? certificateAuthorityPfx = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(request.CertificateAuthorityPfxBase64))
                            certificateAuthorityPfx = Convert.FromBase64String(request.CertificateAuthorityPfxBase64);

                        var invitation = await registry.CreateInvitationAsync(
                            request.GatewayInstanceId,
                            request.GatewayServerPublicKeyHash,
                            request.AuthoritativeRuntimeInstanceId,
                            request.AuthoritativeRuntimeInstallFingerprint,
                            TimeSpan.FromSeconds(request.LifetimeSeconds),
                            request.DisplayName,
                            request.Description,
                            certificateAuthorityPfx,
                            ct);
                        return Results.Ok(invitation);
                    }
                    finally
                    {
                        if (certificateAuthorityPfx is not null)
                            System.Security.Cryptography.CryptographicOperations.ZeroMemory(certificateAuthorityPfx);
                    }
                }
                catch (RemoteRuntimePairingRegistryException exception)
                {
                    return Results.Json(new { code = exception.Code, error = exception.Message }, statusCode: 400);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 400);
                }
            },
            cancellationToken);
    }

    [MapPost("/claim")]
    public static Task<IResult> Claim(
        HttpContext context,
        RemoteRuntimePairingClaimRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "claim",
            RemoteRuntimeBridgePaths.RegistryPrefix,
            ct => registry.ClaimAsync(new RemoteRuntimePairingClaim(
                request.PairId,
                request.Secret,
                request.ProxyRuntimeInstanceId,
                null,
                request.CertificateSigningRequestBase64,
                request.ProofSignatureBase64,
                request.BridgeProtocolMajor), ct),
            cancellationToken);

    [MapPost("/certificate")]
    public static Task<IResult> Certificate(
        HttpContext context,
        RemoteRuntimePairingCertificateRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "issue-certificate",
            RemoteRuntimeBridgePaths.RegistryPrefix,
            async ct =>
            {
                var certificate = await registry.IssueClientCertificateAsync(
                    request.PairId,
                    request.CertificateProofSignatureBase64,
                    ct);
                return new RemoteRuntimePairingCertificateResponse(
                    Convert.ToBase64String(certificate.CertificateDer),
                    certificate.ProxyRuntimePublicKeyHash,
                    certificate.CertificateThumbprint,
                    certificate.NotAfterUtc,
                    certificate.NotBeforeUtc);
            },
            cancellationToken);

    [MapGet("/pairings")]
    public static Task<IResult> List(
        HttpContext context,
        string? gatewayInstanceId,
        string? authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        string? status,
        string? search,
        int? take,
        DateTimeOffset? cursorCreatedAtUtc,
        Guid? cursorId,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "list",
            RemoteRuntimeBridgePaths.RegistryPrefix,
            async ct =>
            {
                RemoteRuntimePairStatus? filterStatus = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!Enum.TryParse<RemoteRuntimePairStatus>(
                            status,
                            ignoreCase: true,
                            out var parsedStatus))
                    {
                        throw new ArgumentException("The pairing status filter is invalid.");
                    }

                    filterStatus = parsedStatus;
                }

                if ((cursorCreatedAtUtc is null) != (cursorId is null))
                    throw new ArgumentException("Both cursor values are required.");

                var cursor = cursorCreatedAtUtc is { } createdAtUtc && cursorId is { } id
                    ? new RemoteRuntimePairingPageCursor(createdAtUtc, id)
                    : null;
                var page = await registry.ListAsync(
                    new RemoteRuntimePairingRegistryFilter(
                        gatewayInstanceId,
                        authoritativeRuntimeInstanceId,
                        proxyRuntimeInstanceId,
                        filterStatus,
                        search),
                    take ?? 50,
                    cursor,
                    ct);
                return new RemoteRuntimeRegistryPageResponse(
                    page.Items.Select(ToSnapshot).ToArray(),
                    page.HasMore,
                    page.Next is { } next
                        ? new RemoteRuntimeRegistryPageCursor(next.CreatedAtUtc, next.Id)
                        : null);
            },
            cancellationToken);

    [MapGet("/pairings/{pairId:guid}")]
    public static async Task<IResult> Find(
        HttpContext context,
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
    {
        return await runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("find", $"/pairings/{pairId:D}"),
            async (_, ct) =>
            {
                var entry = await registry.FindAsync(pairId, ct);
                return entry is null ? Results.NotFound() : Results.Ok(ToSnapshot(entry));
            },
            cancellationToken);
    }

    [MapPut("/pairings/{pairId:guid}")]
    public static Task<IResult> Update(
        HttpContext context,
        Guid pairId,
        RemoteRuntimeRegistryDetailsRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "update",
            $"/pairings/{pairId:D}",
            async ct => ToSnapshot(await registry.UpdateDetailsAsync(
                pairId,
                request.DisplayName,
                request.Description,
                ct)),
            cancellationToken);

    [MapPost("/pairings/{pairId:guid}/renew")]
    public static Task<IResult> Renew(
        HttpContext context,
        Guid pairId,
        RemoteRuntimeRegistryRenewalRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "renew",
            $"/pairings/{pairId:D}/renew",
            async ct => ToSnapshot(await registry.RenewAsync(
                pairId,
                request.ExpiresAtUtc,
                ct,
                request.ProofSignatureBase64)),
            cancellationToken);

    [MapPost("/pairings/{pairId:guid}/last-seen")]
    public static Task<IResult> TouchLastSeen(
        HttpContext context,
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "touch-last-seen",
            $"/pairings/{pairId:D}/last-seen",
            async ct => ToSnapshot(await registry.TouchLastSeenAsync(pairId, ct)),
            cancellationToken);

    [MapDelete("/pairings/{pairId:guid}")]
    public static async Task<IResult> Delete(
        HttpContext context,
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => await runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("delete", $"/pairings/{pairId:D}"),
            async (_, ct) =>
            {
                try
                {
                    await registry.DeleteAsync(pairId, ct);
                    return Results.NoContent();
                }
                catch (RemoteRuntimePairingRegistryException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, error = exception.Message },
                        statusCode: 400);
                }
            },
            cancellationToken);

    [MapPost("/approve")]
    public static Task<IResult> Approve(
        HttpContext context,
        RemoteRuntimeRegistryApprovalRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "approve",
            "/approve",
            ct => registry.ApproveAsync(
                request.PairId,
                request.ProxyRuntimeInstanceId,
                request.AuthoritativeRuntimeInstanceId,
                ct),
            cancellationToken);

    [MapPost("/reject")]
    public static Task<IResult> Reject(
        HttpContext context,
        RemoteRuntimeRegistryRejectionRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "reject",
            "/reject",
            ct => registry.RejectAsync(request.PairId, request.Reason, ct),
            cancellationToken);

    [MapPost("/pairings/{pairId:guid}/reject")]
    public static Task<IResult> RejectPairing(
        HttpContext context,
        Guid pairId,
        RemoteRuntimeRegistryReasonRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "reject-pairing",
            $"/pairings/{pairId:D}/reject",
            async ct => ToSnapshot(await registry.RejectAsync(pairId, request.Reason, ct)),
            cancellationToken);

    [MapPost("/revoke")]
    public static Task<IResult> Revoke(
        HttpContext context,
        RemoteRuntimeRegistryRevocationRequest request,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            context,
            runtimeKernel,
            "revoke",
            "/revoke",
            ct => registry.RevokeAsync(request.PairId, request.Reason, ct),
            cancellationToken);

    [MapGet("/active")]
    public static async Task<IResult> Active(
        HttpContext context,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        string? certificateIdentity,
        string? authoritativeRuntimeInstallFingerprint,
        RemoteRuntimePairingRegistry registry,
        RuntimeKernelAdapter runtimeKernel,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => await runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation("active-lookup", "/active"),
            async (_, ct) =>
            {
                try
                {
                    return Results.Ok(await registry.FindActiveTargetAsync(
                gatewayInstanceId,
                authoritativeRuntimeInstanceId,
                proxyRuntimeInstanceId,
                certificateIdentity,
                authoritativeRuntimeInstallFingerprint,
                ct));
                }
                catch (Exception exception)
                {
                    Microsoft.Extensions.Logging.LoggerExtensions.LogError(
                        loggerFactory.CreateLogger("RemoteRuntimePairingHandlers"),
                        exception,
                        "Remote Runtime pairing active lookup failed for the configured target.");
                    return Results.Json(
                        new { code = "PairingRegistryFailure", error = "The pairing registry lookup failed." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            },
            cancellationToken);

    private static Task<IResult> ExecuteAsync<T>(
        HttpContext context,
        RuntimeKernelAdapter runtimeKernel,
        string operationName,
        string resource,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => runtimeKernel.RunSecurityActionAsync(
            KernelHostEndpoints.CreateExecutionContext(context),
            new SharpClawActionKey("security.remote_pairing.validate"),
            new RuntimeSecurityActionInvocation(operationName, resource),
            async (_, ct) =>
            {
                try
                {
                    return Results.Ok(await operation(ct));
                }
                catch (RemoteRuntimePairingRegistryException exception)
                {
                    return Results.Json(new { code = exception.Code, error = exception.Message }, statusCode: 400);
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 400);
                }
            },
            cancellationToken).AsTask();

    private static RemoteRuntimePairingRegistrySnapshot ToSnapshot(
        RemoteRuntimePairingRegistryEntry entry)
        => new(
            entry.Id,
            entry.PairId,
            entry.Status,
            entry.GatewayInstanceId,
            entry.GatewayServerPublicKeyHash,
            entry.AuthoritativeRuntimeInstanceId,
            entry.AuthoritativeRuntimeInstallFingerprint,
            entry.BridgeProtocolMajor,
            entry.ProxyRuntimeInstanceId,
            entry.ProxyRuntimePublicKeyHash,
            entry.ClientCertificateIdentity,
            entry.DisplayName,
            entry.Description,
            entry.StatusReason,
            entry.CreatedAtUtc,
            entry.ClaimedAtUtc,
            entry.ApprovedAtUtc,
            entry.RenewedAtUtc,
            entry.RevokedAtUtc,
            entry.ExpiresAtUtc,
            entry.LastSeenAtUtc,
            entry.UpdatedAtUtc,
            entry.Revision,
            entry.ClientCertificateIssuedAtUtc,
            entry.ClientCertificateExpiresAtUtc,
            entry.InvitationExpiresAtUtc);
}
