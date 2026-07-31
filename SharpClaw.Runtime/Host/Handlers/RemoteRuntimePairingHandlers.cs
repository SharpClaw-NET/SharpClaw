using Microsoft.AspNetCore.Http;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host.Handlers;

[RouteGroup(RemoteRuntimeBridgePaths.RegistryPrefix)]
public static class RemoteRuntimePairingHandlers
{
    [MapPost("/invitation")]
    public static async Task<IResult> CreateInvitation(
        RemoteRuntimeRegistryInvitationRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
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
                    cancellationToken);
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
    }

    [MapPost("/claim")]
    public static Task<IResult> Claim(
        RemoteRuntimePairingClaimRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => registry.ClaimAsync(new RemoteRuntimePairingClaim(
                request.PairId,
                request.Secret,
                request.ProxyRuntimeInstanceId,
                null,
                request.CertificateSigningRequestBase64,
                request.ProofSignatureBase64,
                request.BridgeProtocolMajor), cancellationToken));

    [MapPost("/certificate")]
    public static Task<IResult> Certificate(
        RemoteRuntimePairingCertificateRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(async ()
            =>
            {
                var certificate = await registry.IssueClientCertificateAsync(
                    request.PairId,
                    request.Secret,
                    cancellationToken);
                return new RemoteRuntimePairingCertificateResponse(
                    Convert.ToBase64String(certificate.CertificateDer),
                    certificate.ProxyRuntimePublicKeyHash,
                    certificate.CertificateThumbprint,
                    certificate.NotAfterUtc,
                    certificate.NotBeforeUtc);
            });

    [MapGet("/pairings")]
    public static Task<IResult> List(
        string? gatewayInstanceId,
        string? authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        string? status,
        string? search,
        int? take,
        DateTimeOffset? cursorCreatedAtUtc,
        Guid? cursorId,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(status)
            && !Enum.TryParse<RemoteRuntimePairStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            return Task.FromResult<IResult>(
                Results.BadRequest(new { error = "The pairing status filter is invalid." }));
        }

        RemoteRuntimePairStatus? filterStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
            filterStatus = Enum.Parse<RemoteRuntimePairStatus>(status, ignoreCase: true);

        if ((cursorCreatedAtUtc is null) != (cursorId is null))
        {
            return Task.FromResult<IResult>(
                Results.BadRequest(new { error = "Both cursor values are required." }));
        }

        var cursor = cursorCreatedAtUtc is { } createdAtUtc && cursorId is { } id
            ? new RemoteRuntimePairingPageCursor(createdAtUtc, id)
            : null;
        return ExecuteAsync(async () =>
        {
            var page = await registry.ListAsync(
                new RemoteRuntimePairingRegistryFilter(
                    gatewayInstanceId,
                    authoritativeRuntimeInstanceId,
                    proxyRuntimeInstanceId,
                    filterStatus,
                    search),
                take ?? 50,
                cursor,
                cancellationToken);
            return new RemoteRuntimeRegistryPageResponse(
                page.Items.Select(ToSnapshot).ToArray(),
                page.HasMore,
                page.Next is { } next
                    ? new RemoteRuntimeRegistryPageCursor(next.CreatedAtUtc, next.Id)
                    : null);
        });
    }

    [MapGet("/pairings/{pairId:guid}")]
    public static async Task<IResult> Find(
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
    {
        var entry = await registry.FindAsync(pairId, cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(ToSnapshot(entry));
    }

    [MapPut("/pairings/{pairId:guid}")]
    public static Task<IResult> Update(
        Guid pairId,
        RemoteRuntimeRegistryDetailsRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
            ToSnapshot(await registry.UpdateDetailsAsync(
                pairId,
                request.DisplayName,
                request.Description,
                cancellationToken)));

    [MapPost("/pairings/{pairId:guid}/renew")]
    public static Task<IResult> Renew(
        Guid pairId,
        RemoteRuntimeRegistryRenewalRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
            ToSnapshot(await registry.RenewAsync(
                pairId,
                request.ExpiresAtUtc,
                cancellationToken)));

    [MapPost("/pairings/{pairId:guid}/last-seen")]
    public static Task<IResult> TouchLastSeen(
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
            ToSnapshot(await registry.TouchLastSeenAsync(pairId, cancellationToken)));

    [MapDelete("/pairings/{pairId:guid}")]
    public static async Task<IResult> Delete(
        Guid pairId,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            await registry.DeleteAsync(pairId, cancellationToken);
            return Results.NoContent();
        }
        catch (RemoteRuntimePairingRegistryException exception)
        {
            return Results.Json(new { code = exception.Code, error = exception.Message }, statusCode: 400);
        }
    }

    [MapPost("/approve")]
    public static Task<IResult> Approve(
        RemoteRuntimeRegistryApprovalRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => registry.ApproveAsync(
                request.PairId,
                request.ProxyRuntimeInstanceId,
                request.AuthoritativeRuntimeInstanceId,
                cancellationToken));

    [MapPost("/reject")]
    public static Task<IResult> Reject(
        RemoteRuntimeRegistryRejectionRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => registry.RejectAsync(request.PairId, request.Reason, cancellationToken));

    [MapPost("/pairings/{pairId:guid}/reject")]
    public static Task<IResult> RejectPairing(
        Guid pairId,
        RemoteRuntimeRegistryReasonRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
            ToSnapshot(await registry.RejectAsync(pairId, request.Reason, cancellationToken)));

    [MapPost("/revoke")]
    public static Task<IResult> Revoke(
        RemoteRuntimeRegistryRevocationRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => registry.RevokeAsync(request.PairId, request.Reason, cancellationToken));

    [MapGet("/active")]
    public static async Task<IResult> Active(
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        string? certificateIdentity,
        string? authoritativeRuntimeInstallFingerprint,
        RemoteRuntimePairingRegistry registry,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await registry.FindActiveTargetAsync(
                gatewayInstanceId,
                authoritativeRuntimeInstanceId,
                proxyRuntimeInstanceId,
                certificateIdentity,
                authoritativeRuntimeInstallFingerprint,
                cancellationToken));
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
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (RemoteRuntimePairingRegistryException exception)
        {
            return Results.Json(new { code = exception.Code, error = exception.Message }, statusCode: 400);
        }
        catch (ArgumentException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: 400);
        }
    }

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
            entry.ClientCertificateExpiresAtUtc);
}
