using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.Websites;
using SharpClaw.Application.Services;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.WebAccess.Services;

/// <summary>
/// CRUD service for <see cref="WebsiteDB"/> entities.
/// Handles credential encryption/decryption and skill association.
/// </summary>
public sealed class WebsiteService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions)
{
    // ═══════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════

    public async Task<WebsiteResponse> CreateAsync(
        string name, string url, string? description,
        CancellationToken ct = default)
    {
        var website = new WebsiteDB
        {
            Name = name,
            Url = url,
            Description = description,
        };
        db.Websites.Add(website);
        await db.SaveChangesAsync(ct);
        return ToResponse(website);
    }

    public async Task<WebsiteResponse> CreateAsync(
        CreateWebsiteRequest request, CancellationToken ct = default)
    {
        var website = new WebsiteDB
        {
            Name = request.Name,
            Url = request.Url,
            Description = request.Description,
            LoginUrl = request.LoginUrl,
            SkillId = request.SkillId,
        };

        if (!string.IsNullOrWhiteSpace(request.Credentials))
            website.EncryptedCredentials =
                ApiKeyEncryptor.Encrypt(request.Credentials, encryptionOptions.Key);

        db.Websites.Add(website);
        await db.SaveChangesAsync(ct);
        return ToResponse(website);
    }

    public async Task<IReadOnlyList<WebsiteResponse>> ListAsync(
        CancellationToken ct = default)
    {
        var websites = await db.Websites
            .OrderBy(w => w.Name)
            .ToListAsync(ct);
        return websites.Select(ToResponse).ToList();
    }

    public async Task<WebsiteResponse?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var website = await db.Websites.FirstOrDefaultAsync(w => w.Id == id, ct);
        return website is null ? null : ToResponse(website);
    }

    public async Task<WebsiteResponse?> UpdateAsync(
        Guid id, string? name, string? url, string? description,
        CancellationToken ct = default)
    {
        var website = await db.Websites.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (website is null) return null;

        if (name is not null) website.Name = name;
        if (url is not null) website.Url = url;
        if (description is not null) website.Description = description;

        await db.SaveChangesAsync(ct);
        return ToResponse(website);
    }

    public async Task<WebsiteResponse?> UpdateAsync(
        Guid id, UpdateWebsiteRequest request, CancellationToken ct = default)
    {
        var website = await db.Websites.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (website is null) return null;

        if (request.Name is not null) website.Name = request.Name;
        if (request.Url is not null) website.Url = request.Url;
        if (request.Description is not null) website.Description = request.Description;
        if (request.LoginUrl is not null) website.LoginUrl = request.LoginUrl;
        if (request.SkillId.HasValue) website.SkillId = request.SkillId;

        if (request.Credentials is not null)
            website.EncryptedCredentials = string.IsNullOrWhiteSpace(request.Credentials)
                ? null
                : ApiKeyEncryptor.Encrypt(request.Credentials, encryptionOptions.Key);

        await db.SaveChangesAsync(ct);
        return ToResponse(website);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var website = await db.Websites.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (website is null) return false;
        db.Websites.Remove(website);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Projection
    // ═══════════════════════════════════════════════════════════════

    private static WebsiteResponse ToResponse(WebsiteDB w) =>
        new(w.Id, w.Name, w.Url, w.Description, w.LoginUrl,
            HasCredentials: w.EncryptedCredentials is not null,
            w.SkillId, w.CreatedAt, w.UpdatedAt);
}
