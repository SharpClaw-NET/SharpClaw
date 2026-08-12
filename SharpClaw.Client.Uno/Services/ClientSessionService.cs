using System.Net.Http.Json;
using System.Text;
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
        HttpResponseMessage response;
        try
        {
            response = await _api.PostAsync("/auth/login", content, cancellationToken);
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

        using (response)
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
        HttpResponseMessage response;
        try
        {
            response = await _api.PostAsync("/auth/refresh", content, cancellationToken);
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

        using (response)
            return await ReadAndEstablishAsync(response, cancellationToken);
    }

    /// <summary>Clears the API token and the authenticated client action context.</summary>
    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.SetAccessTokenAsync(null, cancellationToken);
        }
        catch (Exception exception)
        {
            _api.ForceClearSessionAfterActionFailure();
            throw new ClientSessionCleanupException(exception);
        }
    }

    /// <summary>Runs page work after authentication and clears authority on failure.</summary>
    public async Task RunAuthenticatedContinuationAsync(Func<Task> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        try
        {
            await continuation();
        }
        catch
        {
            await ClearAfterFailureAsync();
            throw;
        }
    }

    /// <summary>Validates the access token subject through the authenticated backend.</summary>
    public async Task<ClientSessionIdentity?> EstablishAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ClientSessionIdentity? identity;
        try
        {
            identity = await EstablishCoreAsync(accessToken, cancellationToken);
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

        if (identity is null)
        {
            await ClearAfterFailureAsync();
            return null;
        }

        return identity;
    }

    private async Task<ClientSessionIdentity?> EstablishCoreAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        // Install the token without authority until /auth/me validates its subject.
        await _api.SetAccessTokenAsync(accessToken, cancellationToken);
        using var response = await _api.GetAsync("/auth/me", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var document = await response.Content.ReadFromJsonAsync<IdentityResponse>(
            JsonOptions,
            cancellationToken);
        if (document is null
            || document.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(document.Username))
            return null;

        var identity = new ClientSessionIdentity(document.Id, document.Username);
        await _api.SetAccessTokenAsync(
            accessToken,
            cancellationToken,
            ClientActionContextSource.ForAuthenticatedUser(
                identity.UserId,
                identity.Username));
        return identity;
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

        LoginResponse? login;
        try
        {
            login = await response.Content.ReadFromJsonAsync<LoginResponse>(
                JsonOptions,
                cancellationToken);
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

        if (string.IsNullOrWhiteSpace(login?.AccessToken))
        {
            await ClearAfterFailureAsync();
            return null;
        }

        ClientSessionIdentity? identity;
        try
        {
            identity = await EstablishCoreAsync(login.AccessToken, cancellationToken);
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

        if (identity is null)
        {
            await ClearAfterFailureAsync();
            return null;
        }

        return new ClientSessionResult(
            identity,
            login.AccessToken,
            login.AccessTokenExpiresAt,
            login.RefreshToken,
            login.RefreshTokenExpiresAt);
    }

    private async Task ClearAfterFailureAsync()
    {
        try
        {
            await _api.SetAccessTokenAsync(null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _api.ForceClearSessionAfterActionFailure();
            throw new ClientSessionCleanupException(exception);
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

internal sealed class ClientSessionCleanupException(Exception innerException)
    : InvalidOperationException("Client session authority cleanup failed.", innerException);
