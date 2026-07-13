using System.Text.Json;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatRequestWorkflowEngineTests
{
    private readonly ChatRequestWorkflowEngine _engine = new();

    [Test]
    public async Task BeginPreparedRequestAsync_LoadsThreadHistoryAppliesHeaderAndPersistsUser()
    {
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var host = new WorkflowHost
        {
            SessionUserId = userId,
            SenderSnapshot = new ChatSenderSnapshot("sender", roleId, "Operator"),
            Header = "[header]\n",
            HistoryResult = new ChatProviderHistoryResult(
                [new ChatCompletionMessage(ChatRoles.Assistant, "prior")],
                MaxMessages: 7,
                MaxCharacters: 500)
        };

        using var state = await _engine.BeginPreparedRequestAsync(
            new ChatPreparedRequest(
                channelId,
                threadId,
                new ChannelDB { Id = channelId, Title = "channel" },
                CreateAgent(),
                CreatePlan(),
                new ChatRequest("hello", ClientType: "cli")),
            host);

        host.BeganThreadId.Should().Be(threadId);
        host.BeginClientType.Should().Be("cli");
        state.History.Select(message => message.Content)
            .Should().Equal("prior", "[header]\nhello");
        state.MaxHistoryMessages.Should().Be(7);
        state.MaxHistoryCharacters.Should().Be(500);
        host.PersistedMessages.Should().ContainSingle();
        var userMessage = host.PersistedMessages[0];
        userMessage.Origin.Should().Be(MessageOrigin.User);
        userMessage.Content.Should().Be("hello");
        userMessage.SenderUserId.Should().Be(userId);
        userMessage.SenderUsername.Should().Be("sender");
        userMessage.PermissionRoleId.Should().Be(roleId);
        userMessage.PermissionRoleName.Should().Be("Operator");

        state.Dispose();
        host.ThreadScope.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task PersistCompletedExchangeAsync_PersistsAssistantRecordsTokensAndReturnsCostedResponse()
    {
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var agent = CreateAgent();
        var userMessage = new ChatMessageEngine().CreateUserMessage(
            channelId,
            threadId,
            new ChatRequest("prompt", ClientType: "web"),
            senderUserId: null,
            senderUsername: "external",
            permissionRoleId: null,
            permissionRoleName: null);
        var host = new WorkflowHost
        {
            ResponseCosts = new ChatResponseCostResult(
                new ChannelCostResponse(channelId, 3, 5, 8, []),
                new ThreadCostResponse(channelId, threadId, 3, 5, 8, []),
                new AgentCostResponse(agent.Id, agent.Name, 3, 5, 8, []))
        };
        var job = new AgentJobResponse(
            Guid.NewGuid(),
            channelId,
            agent.Id,
            ActionKey: "do_work",
            ResourceId: null,
            AgentJobStatus.Completed,
            PermissionClearance.Independent,
            ResultData: "done",
            ErrorLog: null,
            Logs: [],
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null,
            ScriptJson: null,
            WorkingDirectory: null,
            JobCost: null,
            ChannelCost: null);

        var result = await _engine.PersistCompletedExchangeAsync(
            new ChatCompletedExchange(
                channelId,
                threadId,
                new ChatRequest("prompt", ClientType: "web"),
                agent,
                userMessage,
                "answer",
                [job],
                TotalPromptTokens: 3,
                TotalCompletionTokens: 5,
                ProviderMetadataJson: "{\"id\":\"provider\"}"),
            host);

        host.PersistedMessages.Should().ContainSingle();
        var assistant = host.PersistedMessages[0];
        assistant.Origin.Should().Be(MessageOrigin.Assistant);
        assistant.Content.Should().Be("answer");
        assistant.PromptTokens.Should().Be(3);
        assistant.CompletionTokens.Should().Be(5);
        assistant.ProviderMetadataJson.Should().Be("{\"id\":\"provider\"}");
        host.RecordedCurrentExecutionTokens.Should().Be((3, 5));
        host.RecordedAssistantTokens.Should().NotBeNull();
        host.RecordedAssistantTokens!.Value.Should()
            .Be((channelId, threadId, agent.Id, agent.Name, 3, 5));
        host.PublishedNewMessages.Should().Be((threadId, "web"));
        result.Response.UserMessage.Content.Should().Be("prompt");
        result.Response.AssistantMessage.Content.Should().Be("answer");
        result.Response.JobResults.Should().ContainSingle();
        result.Response.ChannelCost!.TotalTokens.Should().Be(8);
        result.Response.ThreadCost!.TotalTokens.Should().Be(8);
        result.Response.AgentCost!.TotalTokens.Should().Be(8);
    }

    [Test]
    public async Task TryPersistPublicErrorAsync_WhenUserMessageExists_PersistsOnlySystemError()
    {
        var host = new WorkflowHost { UserMessageExists = true };
        var result = await _engine.TryPersistPublicErrorAsync(
            new ChatPublicErrorPersistenceRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new ChatRequest("already saved", ClientType: "sse"),
                "failed"),
            host);

        result.Succeeded.Should().BeTrue();
        host.PersistedMessages.Should().ContainSingle();
        host.PersistedMessages[0].Origin.Should().Be(MessageOrigin.System);
        host.PersistedMessages[0].Content.Should().Be("\u26A0 Error: failed");
    }

    [Test]
    public async Task TryPersistExceptionErrorAsync_WhenPersistenceFails_ReturnsFailure()
    {
        var host = new WorkflowHost
        {
            PersistException = new InvalidOperationException("store down")
        };

        var result = await _engine.TryPersistExceptionErrorAsync(
            new ChatExceptionPersistenceRequest(
                Guid.NewGuid(),
                null,
                new ChatRequest("prompt"),
                new InvalidOperationException("provider down"),
                UserMessageAlreadyPersisted: false),
            host);

        result.Succeeded.Should().BeFalse();
        result.Exception.Should().BeSameAs(host.PersistException);
    }

    private static ChatRequestPlan CreatePlan() =>
        new(
            UseNativeTools: true,
            DisableTools: false,
            EnableTools: true,
            SupportsVision: false,
            SystemPrompt: "system",
            CompletionParameters: new CompletionParameters(),
            MaxCompletionTokens: 128,
            ProviderParameters: null,
            ToolAwareness: null,
            ModelCapabilityTags: new HashSet<string>(StringComparer.Ordinal),
            ModelId: Guid.NewGuid(),
            ModelName: "model",
            ProviderKey: "test",
            ProviderName: "Test",
            ProviderEndpoint: null);

    private static AgentDB CreateAgent() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Agent",
        ModelId = Guid.NewGuid()
    };

    private sealed class WorkflowHost : IChatRequestWorkflowHost
    {
        public TrackingDisposable ThreadScope { get; } = new();
        public Guid? BeganThreadId { get; private set; }
        public string? BeginClientType { get; private set; }
        public ChatProviderHistoryResult HistoryResult { get; init; } =
            new([], 50, 100_000);
        public string? Header { get; init; }
        public Guid? SessionUserId { get; init; }
        public ChatSenderSnapshot SenderSnapshot { get; init; } =
            new(null, null, null);
        public List<ChatMessageDB> PersistedMessages { get; } = [];
        public Exception? PersistException { get; init; }
        public bool UserMessageExists { get; init; }
        public (int Prompt, int Completion)? RecordedCurrentExecutionTokens { get; private set; }
        public (Guid ChannelId, Guid? ThreadId, Guid AgentId, string AgentName, int Prompt, int Completion)?
            RecordedAssistantTokens { get; private set; }
        public (Guid ThreadId, string ClientType)? PublishedNewMessages { get; private set; }
        public ChatResponseCostResult ResponseCosts { get; init; } =
            new(new ChannelCostResponse(Guid.Empty, 0, 0, 0, []), null, null);

        public Task<IDisposable?> BeginThreadProcessingAsync(
            Guid threadId,
            string clientType,
            CancellationToken ct)
        {
            BeganThreadId = threadId;
            BeginClientType = clientType;
            return Task.FromResult<IDisposable?>(ThreadScope);
        }

        public Task<ChatProviderHistoryResult> LoadProviderThreadHistoryAsync(
            Guid threadId,
            CancellationToken ct) =>
            Task.FromResult(HistoryResult);

        public Task<string?> BuildChatHeaderAsync(
            ChannelDB channel,
            AgentDB agent,
            ChatRequest request,
            ChatRequestPlan plan,
            CancellationToken ct) =>
            Task.FromResult(Header);

        public Guid? GetSessionUserId() => SessionUserId;

        public Task<ChatSenderSnapshot> LoadSenderSnapshotAsync(
            Guid? senderUserId,
            string? externalDisplayName,
            string? externalUsername,
            CancellationToken ct) =>
            Task.FromResult(SenderSnapshot);

        public Task PersistChatMessagesAsync(
            IReadOnlyList<ChatMessageDB> messages,
            CancellationToken ct)
        {
            if (PersistException is not null)
                throw PersistException;

            PersistedMessages.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<bool> HasUserMessageAsync(
            Guid channelId,
            Guid? threadId,
            string content,
            CancellationToken ct) =>
            Task.FromResult(UserMessageExists);

        public Task RecordTokensForCurrentExecutionAsync(
            int promptTokens,
            int completionTokens,
            CancellationToken ct)
        {
            RecordedCurrentExecutionTokens = (promptTokens, completionTokens);
            return Task.CompletedTask;
        }

        public void RecordAssistantTokens(
            Guid channelId,
            Guid? threadId,
            Guid agentId,
            string agentName,
            int promptTokens,
            int completionTokens) =>
            RecordedAssistantTokens = (
                channelId,
                threadId,
                agentId,
                agentName,
                promptTokens,
                completionTokens);

        public void PublishNewMessages(Guid threadId, string clientType) =>
            PublishedNewMessages = (threadId, clientType);

        public Task<ChatResponseCostResult> GetResponseCostsAsync(
            Guid channelId,
            Guid? threadId,
            Guid agentId,
            string agentName,
            CancellationToken ct) =>
            Task.FromResult(ResponseCosts);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
