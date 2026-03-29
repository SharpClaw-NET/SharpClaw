using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.DTOs.Bots;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class BotIntegrationService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions)
{
    public async Task<IReadOnlyList<BotIntegrationResponse>> ListAsync(CancellationToken ct = default)
    {
        return await db.BotIntegrations
            .Select(b => new BotIntegrationResponse(
                b.Id, b.Name, b.BotType, b.Enabled,
                b.EncryptedBotToken != null,
                b.DefaultChannelId,
                b.DefaultThreadId,
                b.PlatformConfig,
                b.CreatedAt, b.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<BotIntegrationResponse?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var b = await db.BotIntegrations.FindAsync([id], ct);
        return b is null ? null : ToResponse(b);
    }

    public async Task<BotIntegrationResponse?> GetByTypeAsync(BotType type, CancellationToken ct = default)
    {
        var b = await db.BotIntegrations.FirstOrDefaultAsync(x => x.BotType == type, ct);
        return b is null ? null : ToResponse(b);
    }

    public async Task<BotIntegrationResponse> CreateAsync(
        CreateBotIntegrationRequest request, CancellationToken ct = default)
    {
        var bot = new BotIntegrationDB
        {
            Name = request.Name.Trim(),
            BotType = request.BotType,
            Enabled = request.Enabled,
        };

        if (!string.IsNullOrWhiteSpace(request.BotToken))
            bot.EncryptedBotToken = ApiKeyEncryptor.Encrypt(request.BotToken, encryptionOptions.Key);

        if (!string.IsNullOrWhiteSpace(request.PlatformConfig))
            bot.PlatformConfig = request.PlatformConfig;

        db.BotIntegrations.Add(bot);
        await db.SaveChangesAsync(ct);
        return ToResponse(bot);
    }

    public async Task<BotIntegrationResponse> UpdateAsync(
        Guid id, UpdateBotIntegrationRequest request, CancellationToken ct = default)
    {
        var bot = await db.BotIntegrations.FindAsync([id], ct)
            ?? throw new ArgumentException($"Bot integration {id} not found.");

        if (request.Name is not null) bot.Name = request.Name.Trim();
        if (request.Enabled.HasValue) bot.Enabled = request.Enabled.Value;
        if (request.BotToken is not null)
        {
            bot.EncryptedBotToken = string.IsNullOrWhiteSpace(request.BotToken)
                ? null
                : ApiKeyEncryptor.Encrypt(request.BotToken, encryptionOptions.Key);
        }
        if (request.DefaultChannelId.HasValue)
        {
            if (request.DefaultChannelId.Value == Guid.Empty)
            {
                bot.DefaultChannelId = null;
                bot.DefaultThreadId = null;
            }
            else
            {
                bot.DefaultChannelId = request.DefaultChannelId.Value;
                // Auto-resolve thread when channel is assigned
                bot.DefaultThreadId = await ResolveOrCreateThreadAsync(
                    request.DefaultChannelId.Value, ct);
            }
        }
        if (request.DefaultThreadId.HasValue)
        {
            bot.DefaultThreadId = request.DefaultThreadId.Value == Guid.Empty
                ? null
                : request.DefaultThreadId.Value;
        }
        if (request.PlatformConfig is not null)
        {
            bot.PlatformConfig = string.IsNullOrWhiteSpace(request.PlatformConfig)
                ? null
                : request.PlatformConfig;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(bot);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var bot = await db.BotIntegrations.FindAsync([id], ct);
        if (bot is null) return false;
        db.BotIntegrations.Remove(bot);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Ensures a <see cref="BotIntegrationDB"/> row exists for every
    /// <see cref="BotType"/> value. Called on startup to seed the table.
    /// </summary>
    public async Task EnsureAllTypesExistAsync(CancellationToken ct = default)
    {
        var existing = await db.BotIntegrations
            .Select(b => b.BotType)
            .ToHashSetAsync(ct);

        foreach (var type in Enum.GetValues<BotType>())
        {
            if (existing.Contains(type)) continue;
            db.BotIntegrations.Add(new BotIntegrationDB { BotType = type, Name = type.ToString() });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the decrypted bot token for a given type.
    /// Used by the gateway to start bot services.
    /// </summary>
    public async Task<(bool enabled, string? token, Guid? defaultChannelId, Guid? defaultThreadId, string? platformConfig)> GetBotConfigAsync(
        BotType type, CancellationToken ct = default)
    {
        var b = await db.BotIntegrations.FirstOrDefaultAsync(x => x.BotType == type, ct);
        if (b is null) return (false, null, null, null, null);

        var token = b.EncryptedBotToken is not null
            ? ApiKeyEncryptor.Decrypt(b.EncryptedBotToken, encryptionOptions.Key)
            : null;

        return (b.Enabled, token, b.DefaultChannelId, b.DefaultThreadId, b.PlatformConfig);
    }

    private static BotIntegrationResponse ToResponse(BotIntegrationDB b) =>
        new(b.Id, b.Name, b.BotType, b.Enabled, b.EncryptedBotToken is not null,
            b.DefaultChannelId, b.DefaultThreadId, b.PlatformConfig, b.CreatedAt, b.UpdatedAt);

    /// <summary>
    /// Finds the latest thread in a channel, or creates a "Default" thread if none exist.
    /// </summary>
    private async Task<Guid> ResolveOrCreateThreadAsync(Guid channelId, CancellationToken ct)
    {
        var latest = await db.ChatThreads
            .Where(t => t.ChannelId == channelId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (latest.HasValue) return latest.Value;

        var thread = new ChatThreadDB
        {
            Name = "Default",
            ChannelId = channelId,
        };
        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(ct);
        return thread.Id;
    }
}
