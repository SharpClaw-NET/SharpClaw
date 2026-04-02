using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Documents;

// ── Requests ──────────────────────────────────────────────────────

public sealed record CreateDocumentSessionRequest(
    string FilePath,
    string? Name = null,
    string? Description = null);

public sealed record UpdateDocumentSessionRequest(
    string? Name = null,
    string? Description = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record DocumentSessionResponse(
    Guid Id,
    string Name,
    string FilePath,
    DocumentType DocumentType,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
