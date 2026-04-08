using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Databases;

// ── Internal Database ─────────────────────────────────────────────

/// <summary>Request to create an internal (SharpClaw-managed) database resource.</summary>
public sealed record CreateInternalDatabaseRequest(
    string Name,
    DatabaseType DatabaseType,
    /// <summary>Filesystem path or connection detail for the internal database.</summary>
    string Path,
    string? Description = null,
    Guid? SkillId = null);

/// <summary>Request to update an existing internal database resource.</summary>
public sealed record UpdateInternalDatabaseRequest(
    string? Name = null,
    DatabaseType? DatabaseType = null,
    string? Path = null,
    string? Description = null,
    Guid? SkillId = null);

/// <summary>Response DTO for an internal database resource.</summary>
public sealed record InternalDatabaseResponse(
    Guid Id,
    string Name,
    DatabaseType DatabaseType,
    string Path,
    string? Description,
    Guid? SkillId,
    DateTimeOffset CreatedAt);

// ── External Database ─────────────────────────────────────────────

/// <summary>Request to create an external database resource.</summary>
public sealed record CreateExternalDatabaseRequest(
    string Name,
    DatabaseType DatabaseType,
    /// <summary>Plain-text connection string — encrypted before storage.</summary>
    string ConnectionString,
    string? Description = null,
    Guid? SkillId = null);

/// <summary>Request to update an existing external database resource.</summary>
public sealed record UpdateExternalDatabaseRequest(
    string? Name = null,
    DatabaseType? DatabaseType = null,
    /// <summary>Plain-text connection string — re-encrypted before storage. Null = keep existing.</summary>
    string? ConnectionString = null,
    string? Description = null,
    Guid? SkillId = null);

/// <summary>Response DTO for an external database resource.</summary>
public sealed record ExternalDatabaseResponse(
    Guid Id,
    string Name,
    DatabaseType DatabaseType,
    string? Description,
    Guid? SkillId,
    DateTimeOffset CreatedAt);
