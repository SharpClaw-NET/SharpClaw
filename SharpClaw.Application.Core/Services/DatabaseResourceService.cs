using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.Databases;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

/// <summary>
/// CRUD service for <see cref="InternalDatabaseDB"/> and
/// <see cref="ExternalDatabaseDB"/> resources.
/// </summary>
public sealed class DatabaseResourceService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions)
{
    // ═══════════════════════════════════════════════════════════════
    //  Internal Databases — Create
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Creates an internal database resource.</summary>
    public async Task<InternalDatabaseResponse> CreateInternalAsync(
        CreateInternalDatabaseRequest request, CancellationToken ct = default)
    {
        var entity = new InternalDatabaseDB
        {
            Name = request.Name,
            DatabaseType = request.DatabaseType,
            Path = request.Path,
            Description = request.Description,
            SkillId = request.SkillId,
        };

        db.InternalDatabases.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToInternalResponse(entity);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Internal Databases — Read
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Gets an internal database resource by ID.</summary>
    public async Task<InternalDatabaseResponse?> GetInternalByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.InternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        return entity is not null ? ToInternalResponse(entity) : null;
    }

    /// <summary>Lists all internal database resources.</summary>
    public async Task<IReadOnlyList<InternalDatabaseResponse>> ListInternalAsync(
        CancellationToken ct = default)
    {
        var entities = await db.InternalDatabases
            .OrderBy(e => e.Name)
            .ToListAsync(ct);
        return entities.Select(ToInternalResponse).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Internal Databases — Update
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Updates an internal database resource.</summary>
    public async Task<InternalDatabaseResponse?> UpdateInternalAsync(
        Guid id, UpdateInternalDatabaseRequest request, CancellationToken ct = default)
    {
        var entity = await db.InternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null) return null;

        if (request.Name is not null) entity.Name = request.Name;
        if (request.DatabaseType is not null) entity.DatabaseType = request.DatabaseType.Value;
        if (request.Path is not null) entity.Path = request.Path;
        if (request.Description is not null) entity.Description = request.Description;
        if (request.SkillId is not null) entity.SkillId = request.SkillId;

        await db.SaveChangesAsync(ct);
        return ToInternalResponse(entity);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Internal Databases — Delete
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Deletes an internal database resource.</summary>
    public async Task<bool> DeleteInternalAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.InternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null) return false;

        db.InternalDatabases.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  External Databases — Create
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Creates an external database resource with an encrypted connection string.</summary>
    public async Task<ExternalDatabaseResponse> CreateExternalAsync(
        CreateExternalDatabaseRequest request, CancellationToken ct = default)
    {
        var entity = new ExternalDatabaseDB
        {
            Name = request.Name,
            DatabaseType = request.DatabaseType,
            EncryptedConnectionString = ApiKeyEncryptor.Encrypt(
                request.ConnectionString, encryptionOptions.Key),
            Description = request.Description,
            SkillId = request.SkillId,
        };

        db.ExternalDatabases.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToExternalResponse(entity);
    }

    // ═══════════════════════════════════════════════════════════════
    //  External Databases — Read
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Gets an external database resource by ID.</summary>
    public async Task<ExternalDatabaseResponse?> GetExternalByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.ExternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        return entity is not null ? ToExternalResponse(entity) : null;
    }

    /// <summary>Lists all external database resources.</summary>
    public async Task<IReadOnlyList<ExternalDatabaseResponse>> ListExternalAsync(
        CancellationToken ct = default)
    {
        var entities = await db.ExternalDatabases
            .OrderBy(e => e.Name)
            .ToListAsync(ct);
        return entities.Select(ToExternalResponse).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    //  External Databases — Update
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Updates an external database resource.</summary>
    public async Task<ExternalDatabaseResponse?> UpdateExternalAsync(
        Guid id, UpdateExternalDatabaseRequest request, CancellationToken ct = default)
    {
        var entity = await db.ExternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null) return null;

        if (request.Name is not null) entity.Name = request.Name;
        if (request.DatabaseType is not null) entity.DatabaseType = request.DatabaseType.Value;
        if (request.ConnectionString is not null)
            entity.EncryptedConnectionString = ApiKeyEncryptor.Encrypt(
                request.ConnectionString, encryptionOptions.Key);
        if (request.Description is not null) entity.Description = request.Description;
        if (request.SkillId is not null) entity.SkillId = request.SkillId;

        await db.SaveChangesAsync(ct);
        return ToExternalResponse(entity);
    }

    // ═══════════════════════════════════════════════════════════════
    //  External Databases — Delete
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Deletes an external database resource.</summary>
    public async Task<bool> DeleteExternalAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.ExternalDatabases
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null) return false;

        db.ExternalDatabases.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Decryption (used by the Database Access module for execution)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Decrypts the connection string for an external database.
    /// Used by the Database Access module when executing queries.
    /// </summary>
    public async Task<string?> DecryptConnectionStringAsync(
        Guid externalDatabaseId, CancellationToken ct = default)
    {
        var entity = await db.ExternalDatabases
            .FirstOrDefaultAsync(e => e.Id == externalDatabaseId, ct);
        if (entity is null) return null;

        return ApiKeyEncryptor.Decrypt(
            entity.EncryptedConnectionString, encryptionOptions.Key);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mapping
    // ═══════════════════════════════════════════════════════════════

    private static InternalDatabaseResponse ToInternalResponse(InternalDatabaseDB entity) =>
        new(entity.Id, entity.Name, entity.DatabaseType, entity.Path,
            entity.Description, entity.SkillId, entity.CreatedAt);

    private static ExternalDatabaseResponse ToExternalResponse(ExternalDatabaseDB entity) =>
        new(entity.Id, entity.Name, entity.DatabaseType,
            entity.Description, entity.SkillId, entity.CreatedAt);
}
