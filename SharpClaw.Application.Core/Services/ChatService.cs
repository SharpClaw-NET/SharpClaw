using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ChatService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory)
{
    private const int MaxHistoryMessages = 50;

    public async Task<ChatResponse> SendMessageAsync(
        Guid conversationId, ChatRequest request, CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new ArgumentException($"Conversation {conversationId} not found.");

        var model = conversation.Model;
        var provider = model.Provider;
        var agent = conversation.Agent;

        if (string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException("Provider does not have an API key configured.");

        // Build conversation: recent history + new user message
        var history = await db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryMessages)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatCompletionMessage(m.Role, m.Content))
            .ToListAsync(ct);

        history.Add(new ChatCompletionMessage("user", request.Message));

        var apiKey = ApiKeyEncryptor.Decrypt(provider.EncryptedApiKey, encryptionOptions.Key);
        var client = clientFactory.GetClient(provider.ProviderType, provider.ApiEndpoint);

        using var httpClient = httpClientFactory.CreateClient();
        var assistantContent = await client.ChatCompletionAsync(
            httpClient, apiKey, model.Name, agent.SystemPrompt, history, ct);

        // Persist both messages
        var userMessage = new ChatMessageDB
        {
            Role = "user",
            Content = request.Message,
            ConversationId = conversationId
        };

        var assistantMessage = new ChatMessageDB
        {
            Role = "assistant",
            Content = assistantContent,
            ConversationId = conversationId
        };

        db.ChatMessages.Add(userMessage);
        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);

        return new ChatResponse(
            new ChatMessageResponse(userMessage.Role, userMessage.Content, userMessage.CreatedAt),
            new ChatMessageResponse(assistantMessage.Role, assistantMessage.Content, assistantMessage.CreatedAt));
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid conversationId, int limit = 50, CancellationToken ct = default)
    {
        return await db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageResponse(m.Role, m.Content, m.CreatedAt))
            .ToListAsync(ct);
    }
}
