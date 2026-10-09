using System.Text.Json;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Presentation;


public sealed record UnoDirectChatRequest(
    string Message,
    Guid? ConversationId = null);
