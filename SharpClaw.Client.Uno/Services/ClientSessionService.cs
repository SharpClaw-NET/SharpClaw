using System.Text;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharpClaw.Services;

/// <summary>Owns client token installation and backend identity validation.</summary>
public sealed class ClientSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SharpClawApiClient _api;

    public ClientSessionService(SharpClawApiClient api)
    {
        _api = api;
    }

    /// <summary>Authenticates credentials and validates the returned token subject.</summary>
    public async Task<ClientSessionResult?> LoginAsync(
        string username,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(
            new { username, password, rememberMe },
            JsonOptions);
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await _api.PostAsync("/auth/login", content, cancellationToken);
        return await ReadAndEstablishAsync(response, cancellationToken);
    }

    /// <summary>Refreshes a token and validates the subject returned by the backend.</summary>
    public async Task<ClientSessionResult?> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(
            new { refreshToken },
            JsonOptions);
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await _api.PostAsync("/auth/refresh", content, cancellationToken);
        return await ReadAndEstablishAsync(response, cancellationToken);
    }

    /// <summary>Clears the API token and the authenticated client action context.</summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        _api.SetAccessTokenAsync(null, cancellationToken);

    /// <summary>Validates the access token subject through the authenticated backend.</summary>
    public async Task<ClientSessionIdentity?> EstablishAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await ClearAfterFailureAsync();
            return null;
        }

        try
        {
            // Install the token without authority until /auth/me validates its subject.
            await _api.SetAccessTokenAsync(accessToken, cancellationToken);
            using var response = await _api.GetAsync("/auth/me", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await ClearAfterFailureAsync();
                return null;
            }

            var document = await response.Content.ReadFromJsonAsync<IdentityResponse>(
                JsonOptions,
                cancellationToken);
            if (document is null
                || document.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(document.Username))
            {
                await ClearAfterFailureAsync();
                return null;
            }

            var identity = new ClientSessionIdentity(document.Id, document.Username);
            await _api.SetAccessTokenAsync(
                accessToken,
                cancellationToken,
                ClientActionContextSource.ForAuthenticatedUser(
                    identity.UserId,
                    identity.Username));
            return identity;
        }
        catch (OperationCanceledException)
        {
            await ClearAfterFailureAsync();
            throw;
        }
        catch
        {
            await ClearAfterFailureAsync();
            return null;
        }
    }

    private async Task<ClientSessionResult?> ReadAndEstablishAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ClearAfterFailureAsync();
            return null;
        }

        try
        {
            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
                JsonOptions,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(login?.AccessToken))
            {
                await ClearAfterFailureAsync();
                return null;
            }

            var identity = await EstablishAsync(login.AccessToken, cancellationToken);
            return identity is null
                ? null
                : new ClientSessionResult(
                    identity,
                    login.AccessToken,
                    login.AccessTokenExpiresAt,
                    login.RefreshToken,
                    login.RefreshTokenExpiresAt);
        }
        catch (OperationCanceledException)
        {
            await ClearAfterFailureAsync();
            throw;
        }
        catch
        {
            await ClearAfterFailureAsync();
            return null;
        }
    }

    private async Task ClearAfterFailureAsync()
    {
        try
        {
            await _api.SetAccessTokenAsync(null, CancellationToken.None);
        }
        catch
        {
            // Keep the session fail-closed if cleanup itself fails.
        }
    }

    private sealed class LoginResponse
    {
        public string? AccessToken { get; set; }
        public DateTimeOffset? AccessTokenExpiresAt { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
    }

    private sealed class IdentityResponse
    {
        public Guid Id { get; set; }
        public string? Username { get; set; }
    }
}

public sealed class ClientSessionIdentity
{
    public ClientSessionIdentity(Guid userId, string username)
    {
        UserId = userId;
        Username = username;
    }

    public Guid UserId { get; }
    public string Username { get; }
}

public sealed class ClientSessionResult
{
    public ClientSessionResult(
        ClientSessionIdentity identity,
        string accessToken,
        DateTimeOffset? accessTokenExpiresAt,
        string? refreshToken,
        DateTimeOffset? refreshTokenExpiresAt)
    {
        Identity = identity;
        AccessToken = accessToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshToken = refreshToken;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
    }

    public ClientSessionIdentity Identity { get; }
    public string AccessToken { get; }
    public DateTimeOffset? AccessTokenExpiresAt { get; }
    public string? RefreshToken { get; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; }
}
