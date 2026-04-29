using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class GitHubCopilotApiClient : OpenAiCompatibleApiClient, IDeviceCodeAuthClient
{
    private const string GitHubClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string UserAgent = "SharpClaw/1.0";

    protected override string ApiEndpoint => "https://api.githubcopilot.com";
    public override string ProviderKey => WellKnownProviderKeys.GitHubCopilot;

    // Cached Copilot API token and its expiry
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt;

    /// <summary>
    /// Exchanges the GitHub OAuth token for a short-lived Copilot API token.
    /// Tokens are cached until they expire.
    /// </summary>
    protected override async ValueTask<string> ResolveApiKeyAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _cachedToken;

        using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CopilotTokenResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from Copilot token endpoint.");

        _cachedToken = body.Token
            ?? throw new InvalidOperationException("Copilot token response did not contain a token.");

        // Refresh a minute early to avoid edge-case expiry during a request
        _tokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(body.ExpiresAt) - TimeSpan.FromMinutes(1);

        return _cachedToken;
    }

    protected override void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Add("Copilot-Integration-Id", "vscode-chat");
        request.Headers.Add("Editor-Version", "vscode/1.99.0");
    }

    /// <summary>
    /// GitHub Copilot's gateway only exposes <c>/v1/responses</c> for OpenAI's
    /// modern generations (gpt-5+, o-series, codex). Other hosted families
    /// (Claude, Gemini, Grok, legacy gpt-3.5/gpt-4, MS routers, embeddings,
    /// etc.) reject the Responses API with HTTP 400, so route them through
    /// Chat Completions instead.
    /// </summary>
    protected override bool UseResponsesApi(string model)
    {
        if (RequiresLegacyChatCompletions(model))
            return false;

        var name = model.ToLowerInvariant();
        return name.StartsWith("gpt-5")
            || name.StartsWith("o1")
            || name.StartsWith("o3")
            || name.StartsWith("o4")
            || name.StartsWith("codex");
    }

    public async Task<DeviceCodeSession> StartDeviceCodeFlowAsync(HttpClient httpClient, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubClientId,
                ["scope"] = "read:user"
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GitHubDeviceCodeResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from GitHub device code endpoint.");

        return new DeviceCodeSession(
            body.DeviceCode,
            body.UserCode,
            body.VerificationUri,
            body.ExpiresIn,
            body.Interval);
    }

    public async Task<string> PollForAccessTokenAsync(HttpClient httpClient, DeviceCodeSession session, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(session.IntervalSeconds), ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GitHubClientId,
                    ["device_code"] = session.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadFromJsonAsync<GitHubAccessTokenResponse>(ct);

            if (body?.AccessToken is not null)
                return body.AccessToken;

            if (body?.Error is "expired_token")
                throw new TimeoutException("Device code expired before the user completed authorization.");

            // "authorization_pending" or "slow_down" – keep polling
            if (body?.Error is "slow_down")
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        throw new TimeoutException("Device code flow timed out.");
    }

    private sealed record GitHubDeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record GitHubAccessTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record CopilotTokenResponse(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);
}
