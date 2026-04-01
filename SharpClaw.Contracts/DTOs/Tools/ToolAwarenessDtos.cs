namespace SharpClaw.Contracts.DTOs.Tools;

public sealed record CreateToolAwarenessSetRequest(
    string Name,
    Dictionary<string, bool>? Tools = null);

public sealed record UpdateToolAwarenessSetRequest(
    string? Name = null,
    Dictionary<string, bool>? Tools = null);

public sealed record ToolAwarenessSetResponse(
    Guid Id,
    string Name,
    Dictionary<string, bool> Tools,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
