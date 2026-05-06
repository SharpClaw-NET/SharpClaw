using System;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>A channel context returned by GET /channel-contexts.</summary>
internal sealed class ContextDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>
/// A channel returned by GET /channels. The backend serializes this as
/// <c>ChannelResponse</c>; the user-visible label lives on <see cref="Title"/>.
/// </summary>
internal sealed class ChannelDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? ContextId { get; set; }
}

/// <summary>A thread returned by GET /channels/{id}/threads.</summary>
internal sealed class ThreadDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ChannelId { get; set; }
}

/// <summary>
/// A persisted chat message returned by history endpoints. Mirrors
/// <c>SharpClaw.Contracts.DTOs.Chat.ChatMessageResponse</c> so sender
/// metadata is available to the UI.
/// </summary>
internal sealed class ChatMessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public Guid? SenderUserId { get; set; }
    public string? SenderUsername { get; set; }
    public Guid? SenderAgentId { get; set; }
    public string? SenderAgentName { get; set; }
    public string? ClientType { get; set; }
}

/// <summary>The payload sent to the chat stream endpoints.</summary>
internal sealed class ChatRequestDto
{
    public string Message { get; set; } = string.Empty;
    /// <summary>Free-form client identifier persisted on the message.</summary>
    public string ClientType { get; set; } = "VS2026";
}
