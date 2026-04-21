using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// OpenAI-compatible client for a user-managed Ollama server.
/// Defaults to <c>http://localhost:11434</c> when no endpoint is
/// stored on the provider record. Overrides model listing to use
/// Ollama's <c>GET /api/tags</c> endpoint.
/// </summary>
public sealed class OllamaApiClient(string? apiEndpoint = null) : OpenAiCompatibleApiClient
{
    private const string DefaultEndpoint = "http://localhost:11434";

    protected override string ApiEndpoint { get; } =
        string.IsNullOrWhiteSpace(apiEndpoint)
            ? DefaultEndpoint
            : apiEndpoint.TrimEnd('/');

    public override ProviderType ProviderType => ProviderType.Ollama;

    public override async Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient,
        string apiKey,
        CancellationToken ct = default)
    {
        var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>(
            $"{ApiEndpoint}/api/tags", ct);

        return response?.Models
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList()
            ?? [];
    }

    // ── Ollama /api/tags response shape ─────────────────────────

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaModel> Models);

    private sealed record OllamaModel(
        [property: JsonPropertyName("name")] string Name);
}
