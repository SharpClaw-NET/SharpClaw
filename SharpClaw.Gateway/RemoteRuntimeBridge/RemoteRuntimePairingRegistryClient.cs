using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Gateway.RemoteRuntimeBridge;

internal interface IRemoteRuntimePairingRegistryClient : IAsyncDisposable
{
    Task<RemoteRuntimePairingInvitation> CreateInvitationAsync(
        RemoteRuntimeRegistryInvitationRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> ClaimAsync(
        RemoteRuntimePairingClaimRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingCertificateResponse> IssueClientCertificateAsync(
        RemoteRuntimePairingCertificateRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> ApproveAsync(
        RemoteRuntimeRegistryApprovalRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> RevokeAsync(
        RemoteRuntimeRegistryRevocationRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot?> FindActiveAsync(
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken);

    Task<RemoteRuntimeRegistryPageResponse> ListAsync(
        string? gatewayInstanceId,
        string? authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        RemoteRuntimePairStatus? status,
        string? search,
        int take,
        RemoteRuntimeRegistryPageCursor? cursor,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot?> FindAsync(
        Guid pairId,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> UpdateAsync(
        Guid pairId,
        RemoteRuntimeRegistryDetailsRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> RenewAsync(
        Guid pairId,
        RemoteRuntimeRegistryRenewalRequest request,
        CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> RejectAsync(
        Guid pairId,
        string reason,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid pairId, CancellationToken cancellationToken);

    Task<RemoteRuntimePairingRegistrySnapshot> RequireActiveCertificateAsync(
        X509Certificate2 certificate,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken);

    void Invalidate(Guid pairId);
}

internal sealed class RemoteRuntimePairingRegistryClient : IRemoteRuntimePairingRegistryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _validationCache = new(StringComparer.Ordinal);

    public RemoteRuntimePairingRegistryClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static RemoteRuntimePairingRegistryClient Create(RemoteRuntimeBridgeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Uri.TryCreate(target.TargetBaseUrl, UriKind.Absolute, out var baseAddress))
            throw new InvalidOperationException("The authoritative Runtime URL is invalid.");

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", target.AuthoritativeApiKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Gateway-Token", target.AuthoritativeGatewayToken);
        return new RemoteRuntimePairingRegistryClient(client);
    }

    public Task<RemoteRuntimePairingInvitation> CreateInvitationAsync(
        RemoteRuntimeRegistryInvitationRequest request,
        CancellationToken cancellationToken)
        => PostAsync<RemoteRuntimeRegistryInvitationRequest, RemoteRuntimePairingInvitation>(
            RemoteRuntimeBridgePaths.RegistryInvitation,
            request,
            cancellationToken);

    public Task<RemoteRuntimePairingRegistrySnapshot> ClaimAsync(
        RemoteRuntimePairingClaimRequest request,
        CancellationToken cancellationToken)
        => PostAsync<RemoteRuntimePairingClaimRequest, RemoteRuntimePairingRegistrySnapshot>(
            RemoteRuntimeBridgePaths.RegistryClaim,
            request,
            cancellationToken);

    public Task<RemoteRuntimePairingCertificateResponse> IssueClientCertificateAsync(
        RemoteRuntimePairingCertificateRequest request,
        CancellationToken cancellationToken)
        => PostAsync<RemoteRuntimePairingCertificateRequest, RemoteRuntimePairingCertificateResponse>(
            RemoteRuntimeBridgePaths.RegistryCertificate,
            request,
            cancellationToken);

    public async Task<RemoteRuntimePairingRegistrySnapshot> ApproveAsync(
        RemoteRuntimeRegistryApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await PostAsync<RemoteRuntimeRegistryApprovalRequest, RemoteRuntimePairingRegistrySnapshot>(
            RemoteRuntimeBridgePaths.RegistryApprove,
            request,
            cancellationToken);
        Invalidate(result.PairId);
        return result;
    }

    public async Task<RemoteRuntimePairingRegistrySnapshot> RevokeAsync(
        RemoteRuntimeRegistryRevocationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await PostAsync<RemoteRuntimeRegistryRevocationRequest, RemoteRuntimePairingRegistrySnapshot>(
            RemoteRuntimeBridgePaths.RegistryRevoke,
            request,
            cancellationToken);
        Invalidate(result.PairId);
        return result;
    }

    public async Task<RemoteRuntimePairingRegistrySnapshot?> FindActiveAsync(
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken)
    {
        RequireText(gatewayInstanceId, nameof(gatewayInstanceId));
        RequireText(authoritativeRuntimeInstanceId, nameof(authoritativeRuntimeInstanceId));
        var path = $"{RemoteRuntimeBridgePaths.RegistryActive}?gatewayInstanceId={Uri.EscapeDataString(gatewayInstanceId)}&authoritativeRuntimeInstanceId={Uri.EscapeDataString(authoritativeRuntimeInstanceId)}";
        return await GetAsync<RemoteRuntimePairingRegistrySnapshot>(path, cancellationToken);
    }

    public async Task<RemoteRuntimeRegistryPageResponse> ListAsync(
        string? gatewayInstanceId,
        string? authoritativeRuntimeInstanceId,
        string? proxyRuntimeInstanceId,
        RemoteRuntimePairStatus? status,
        string? search,
        int take,
        RemoteRuntimeRegistryPageCursor? cursor,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { $"take={take}" };
        AddQuery(query, "gatewayInstanceId", gatewayInstanceId);
        AddQuery(query, "authoritativeRuntimeInstanceId", authoritativeRuntimeInstanceId);
        AddQuery(query, "proxyRuntimeInstanceId", proxyRuntimeInstanceId);
        AddQuery(query, "status", status?.ToString());
        AddQuery(query, "search", search);
        if (cursor is { } pageCursor)
        {
            AddQuery(query, "cursorCreatedAtUtc", pageCursor.CreatedAtUtc.ToString("O"));
            AddQuery(query, "cursorId", pageCursor.Id.ToString("D"));
        }

        return await GetAsync<RemoteRuntimeRegistryPageResponse>(
                $"{RemoteRuntimeBridgePaths.RegistryPairings}?{string.Join('&', query)}",
                cancellationToken)
            ?? throw new RemoteRuntimePairingException(
                "PairingResponseInvalid",
                "The authoritative Runtime returned an empty pairing page.");
    }

    public Task<RemoteRuntimePairingRegistrySnapshot?> FindAsync(
        Guid pairId,
        CancellationToken cancellationToken)
        => GetAsync<RemoteRuntimePairingRegistrySnapshot>(PairingPath(pairId), cancellationToken);

    public async Task<RemoteRuntimePairingRegistrySnapshot> UpdateAsync(
        Guid pairId,
        RemoteRuntimeRegistryDetailsRequest request,
        CancellationToken cancellationToken)
        => await PutAsync<RemoteRuntimeRegistryDetailsRequest, RemoteRuntimePairingRegistrySnapshot>(
            PairingPath(pairId),
            request,
            cancellationToken)
            ?? throw InvalidResponse();

    public async Task<RemoteRuntimePairingRegistrySnapshot> RenewAsync(
        Guid pairId,
        RemoteRuntimeRegistryRenewalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await PostAsync<RemoteRuntimeRegistryRenewalRequest, RemoteRuntimePairingRegistrySnapshot>(
            PairingRenewPath(pairId),
            request,
            cancellationToken);
        Invalidate(result.PairId);
        return result;
    }

    public async Task<RemoteRuntimePairingRegistrySnapshot> RejectAsync(
        Guid pairId,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = await PostAsync<RemoteRuntimeRegistryRejectionRequest, RemoteRuntimePairingRegistrySnapshot>(
            PairingRejectPath(pairId),
            new RemoteRuntimeRegistryRejectionRequest(pairId, reason),
            cancellationToken);
        Invalidate(result.PairId);
        return result;
    }

    public async Task DeleteAsync(Guid pairId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(PairingPath(pairId), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
        else
            await ReadResponseAsync<object>(response, cancellationToken);
        Invalidate(pairId);
    }

    public async Task<RemoteRuntimePairingRegistrySnapshot> RequireActiveCertificateAsync(
        X509Certificate2 certificate,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var key = certificate.GetECDsaPublicKey()
            ?? throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The client certificate does not contain an ECDSA public key.");
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(key);
        var cacheKey = gatewayInstanceId + "|" + authoritativeRuntimeInstanceId + "|" + publicKeyHash;
        if (_validationCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return cached.Entry;
        }

        var entry = await FindActiveAsync(
            gatewayInstanceId,
            authoritativeRuntimeInstanceId,
            cancellationToken);
        if (entry is null
            || !entry.IsActive(DateTimeOffset.UtcNow)
            || !string.Equals(entry.ProxyRuntimePublicKeyHash, publicKeyHash, StringComparison.Ordinal))
        {
            throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The client certificate is not active for this Runtime target.");
        }

        if (_validationCache.Count >= 256)
        {
            foreach (var item in _validationCache)
            {
                if (item.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    _validationCache.TryRemove(item.Key, out _);
            }
        }

        _validationCache[cacheKey] = new CacheEntry(
            entry,
            DateTimeOffset.UtcNow.AddSeconds(5));
        return entry;
    }

    public void Invalidate(Guid pairId)
    {
        foreach (var item in _validationCache)
        {
            if (item.Value.Entry.PairId == pairId)
                _validationCache.TryRemove(item.Key, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<TResponse?> GetAsync<TResponse>(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ReadResponseAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            path,
            request,
            JsonOptions,
            cancellationToken);
        return await ReadResponseAsync<TResponse>(response, cancellationToken)
            ?? throw new RemoteRuntimePairingException(
                "PairingResponseInvalid",
                "The authoritative Runtime returned an empty response.");
    }

    private async Task<TResponse?> PutAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            path,
            request,
            JsonOptions,
            cancellationToken);
        return await ReadResponseAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<TResponse?> ReadResponseAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<RemoteRuntimeErrorResponse>(
                JsonOptions,
                cancellationToken);
            throw new RemoteRuntimePairingException(
                error?.Code ?? "PairingRequestFailed",
                $"The authoritative Runtime returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
    }

    private static void AddQuery(
        ICollection<string> query,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            query.Add($"{name}={Uri.EscapeDataString(value)}");
    }

    private static string PairingPath(Guid pairId)
        => $"{RemoteRuntimeBridgePaths.RegistryPairings}/{pairId:D}";

    private static string PairingRenewPath(Guid pairId)
        => $"{PairingPath(pairId)}/renew";

    private static string PairingRejectPath(Guid pairId)
        => $"{RemoteRuntimeBridgePaths.RegistryPairings}/{pairId:D}/reject";

    private static RemoteRuntimePairingException InvalidResponse()
        => new(
            "PairingResponseInvalid",
            "The authoritative Runtime returned an empty pairing response.");

    private sealed record CacheEntry(
        RemoteRuntimePairingRegistrySnapshot Entry,
        DateTimeOffset ExpiresAtUtc);

    private sealed record RemoteRuntimeErrorResponse(string? Code, string? Error);
}
