using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Models;

public sealed record CreateModelRequest(
    string Name,
    Guid ProviderId,
    ModelCapability Capabilities = ModelCapability.Chat,
    string? CustomId = null);

public sealed record UpdateModelRequest(
    string? Name = null,
    ModelCapability? Capabilities = null,
    string? CustomId = null);

public sealed record ModelResponse(
    Guid Id,
    string Name,
    Guid ProviderId,
    string ProviderName,
    ModelCapability Capabilities,
    string? CustomId = null);
