using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Providers;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class EfProviderCostHost(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory) : IProviderCostHost
{
    public async Task<ProviderCostProviderConfiguration?> LoadProviderAsync(
        Guid providerId,
        CancellationToken ct)
    {
        return await db.Providers
            .Where(provider => provider.Id == providerId)
            .Select(provider => (ProviderCostProviderConfiguration?)new ProviderCostProviderConfiguration(
                provider.Id,
                provider.Name,
                provider.ProviderKey,
                provider.EncryptedApiKey))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderCostProviderConfiguration>> ListProvidersForCostAsync(
        bool includeAll,
        CancellationToken ct)
    {
        var query = db.Providers.AsQueryable();
        if (!includeAll)
        {
            query = query.Where(provider =>
                provider.EncryptedApiKey != null
                && provider.EncryptedApiKey != "");
        }

        return await query
            .OrderBy(provider => provider.Name)
            .Select(provider => new ProviderCostProviderConfiguration(
                provider.Id,
                provider.Name,
                provider.ProviderKey,
                provider.EncryptedApiKey))
            .ToListAsync(ct);
    }

    public IProviderPlugin? GetProviderPlugin(string providerKey)
    {
        return clientFactory.GetPlugin(providerKey);
    }

    public string UnprotectProviderSecret(string protectedSecret)
    {
        return ApiKeyEncryptor.DecryptOrPassthrough(
            protectedSecret,
            encryptionOptions.Key);
    }

    public async Task<ProviderCostResult?> GetCostsAsync(
        IProviderCostFeed costFeed,
        string apiKey,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken ct)
    {
        using var httpClient = httpClientFactory.CreateClient();
        return await costFeed.GetCostsAsync(
            httpClient,
            apiKey,
            periodStart,
            periodEnd,
            ct);
    }
}
