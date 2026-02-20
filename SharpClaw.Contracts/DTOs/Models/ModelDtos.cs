using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Models;

public sealed record CreateModelRequest(
    string Name,
    Guid ProviderId,
    ModelCapability Capabilities = ModelCapability.Chat);

public sealed record UpdateModelRequest(
    string? Name = null,
    ModelCapability? Capabilities = null);

public sealed record ModelResponse(
    Guid Id,
    string Name,
    Guid ProviderId,
    string ProviderName,
    ModelCapability Capabilities);
