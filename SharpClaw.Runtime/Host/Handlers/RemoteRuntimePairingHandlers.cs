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
                string.Empty,
                request.CertificateSigningRequestBase64,
                request.ProofSignatureBase64), cancellationToken));

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
                    certificate.NotAfterUtc);
            });

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
                request.ClientCertificateIdentity,
                cancellationToken));

    [MapPost("/reject")]
    public static Task<IResult> Reject(
        RemoteRuntimeRegistryRejectionRequest request,
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => registry.RejectAsync(request.PairId, request.Reason, cancellationToken));

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
        RemoteRuntimePairingRegistry registry,
        CancellationToken cancellationToken)
        => Results.Ok(await registry.FindActiveTargetAsync(
            gatewayInstanceId,
            authoritativeRuntimeInstanceId,
            cancellationToken));

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
}
