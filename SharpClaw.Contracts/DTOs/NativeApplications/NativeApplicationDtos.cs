namespace SharpClaw.Contracts.DTOs.NativeApplications;

// ── Requests ──────────────────────────────────────────────────────

public sealed record CreateNativeApplicationRequest(
    string Name,
    string ExecutablePath,
    string? Alias = null,
    string? Description = null);

public sealed record UpdateNativeApplicationRequest(
    string? Name = null,
    string? ExecutablePath = null,
    string? Alias = null,
    string? Description = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record NativeApplicationResponse(
    Guid Id,
    string Name,
    string ExecutablePath,
    string? Alias,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
