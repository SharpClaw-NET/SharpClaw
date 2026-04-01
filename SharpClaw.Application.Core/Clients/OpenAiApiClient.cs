using System.Net.Http.Headers;
using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class OpenAiApiClient : OpenAiCompatibleApiClient, IProviderCostClient
{
    protected override string ApiEndpoint => "https://api.openai.com/v1";
    public override ProviderType ProviderType => ProviderType.OpenAI;

    /// <summary>
    /// Prefer the Responses API for all models except legacy GPT-3.5/GPT-4
    /// families that predate it.
    /// </summary>
    protected override bool UseResponsesApi(string model)
        => !RequiresLegacyChatCompletions(model);

    /// <summary>
    /// Fetches cost data from the OpenAI Organization Costs API.
    /// Requires an admin API key; returns <see langword="null"/> if the
    /// key lacks admin permissions (HTTP 401/403).
    /// </summary>
    public async Task<ProviderCostResult?> GetCostsAsync(
        HttpClient httpClient,
        string apiKey,
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        CancellationToken ct = default)
    {
        var startUnix = startTime.ToUnixTimeSeconds();
        var url = $"{ApiEndpoint}/organization/costs?start_time={startUnix}&bucket_width=1d&limit=90";
        if (endTime is not null)
            url += $"&end_time={endTime.Value.ToUnixTimeSeconds()}";

        var allBuckets = new List<ProviderCostDailyBucket>();
        decimal total = 0;
        string currency = "usd";
        string? nextPage = null;

        // Paginate through all results
        do
        {
            var requestUrl = nextPage is not null ? $"{url}&page={nextPage}" : url;

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, ct);
            }
            catch
            {
                return null;
            }

            // Admin key not available — graceful fallback
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                foreach (var bucket in data.EnumerateArray())
                {
                    var bucketStart = DateTimeOffset.FromUnixTimeSeconds(
                        bucket.GetProperty("start_time").GetInt64());
                    var bucketEnd = DateTimeOffset.FromUnixTimeSeconds(
                        bucket.GetProperty("end_time").GetInt64());

                    decimal bucketTotal = 0;

                    if (bucket.TryGetProperty("results", out var results))
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            if (result.TryGetProperty("amount", out var amount))
                            {
                                var value = amount.GetProperty("value").GetDecimal();
                                bucketTotal += value;

                                if (amount.TryGetProperty("currency", out var curr))
                                    currency = curr.GetString() ?? "usd";
                            }
                        }
                    }

                    total += bucketTotal;
                    allBuckets.Add(new ProviderCostDailyBucket(bucketStart, bucketEnd, bucketTotal));
                }
            }

            nextPage = root.TryGetProperty("next_page", out var np)
                && np.ValueKind == JsonValueKind.String
                    ? np.GetString()
                    : null;

        } while (nextPage is not null);

        return new ProviderCostResult(total, currency, allBuckets);
    }
}
