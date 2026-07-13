using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Services;

[TestFixture]
public sealed class ChatProviderRoundExecutorTests
{
    [Test]
    public async Task CompleteAsync_ForwardsSemanticRequest()
    {
        var client = new RecordingProviderClient
        {
            PlainResult = new ChatCompletionResult
            {
                Content = "plain",
                Usage = new TokenUsage(1, 2)
            }
        };
        var executor = new ChatProviderRoundExecutor(client);
        var history = new[]
        {
            new ChatCompletionMessage("user", "hello")
        };

        var result = await executor.CompleteAsync(
            new ChatProviderCompletionRequest(
                "model",
                "system",
                history,
                MaxCompletionTokens: 128,
                ProviderParameters: null,
                CompletionParameters: null),
            CancellationToken.None);

        result.Content.Should().Be("plain");
        client.LastModel.Should().Be("model");
        client.LastSystemPrompt.Should().Be("system");
        client.LastHistory.Should().BeSameAs(history);
        client.PlainCalls.Should().Be(1);
    }

    [Test]
    public async Task CompleteWithToolsAsync_ForwardsToolRequest()
    {
        var client = new RecordingProviderClient
        {
            ToolResult = new ChatCompletionResult
            {
                Content = "tool",
                Usage = new TokenUsage(3, 4)
            }
        };
        var executor = new ChatProviderRoundExecutor(client);
        var messages = new[]
        {
            new ToolAwareMessage
            {
                Role = "user",
                Content = "run tool"
            }
        };
        var tools = new[]
        {
            new ChatToolDefinition(
                "alpha",
                "Alpha",
                Json("""{"type":"object"}"""))
        };

        var result = await executor.CompleteWithToolsAsync(
            new ChatProviderToolCompletionRequest(
                "tool-model",
                "tool-system",
                messages,
                tools,
                MaxCompletionTokens: 64,
                ProviderParameters: null,
                CompletionParameters: null),
            CancellationToken.None);

        result.Content.Should().Be("tool");
        client.LastToolMessages.Should().BeSameAs(messages);
        client.LastTools.Should().BeSameAs(tools);
        client.ToolCalls.Should().Be(1);
    }

    [Test]
    public async Task StreamWithToolsAsync_ForwardsStreamingToolRequest()
    {
        var client = new RecordingProviderClient
        {
            StreamingDeltas = ["a", "b"],
            StreamingResult = new ChatCompletionResult
            {
                Content = "ab",
                Usage = new TokenUsage(5, 6)
            }
        };
        var executor = new ChatProviderRoundExecutor(client);
        var messages = Array.Empty<ToolAwareMessage>();
        var tools = Array.Empty<ChatToolDefinition>();

        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in executor.StreamWithToolsAsync(
            new ChatProviderToolCompletionRequest(
                "stream-model",
                "stream-system",
                messages,
                tools,
                MaxCompletionTokens: 32,
                ProviderParameters: null,
                CompletionParameters: null),
            CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        chunks.Select(chunk => chunk.Delta).Should().Equal("a", "b", null);
        chunks[^1].Finished?.Content.Should().Be("ab");
        client.LastToolMessages.Should().BeSameAs(messages);
        client.LastTools.Should().BeSameAs(tools);
        client.StreamingCalls.Should().Be(1);
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RecordingProviderClient : IProviderApiClient
    {
        public string ProviderKey => "test";

        public ChatCompletionResult PlainResult { get; init; } = new();
        public ChatCompletionResult ToolResult { get; init; } = new();
        public IReadOnlyList<string> StreamingDeltas { get; init; } = [];
        public ChatCompletionResult StreamingResult { get; init; } = new();

        public int PlainCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public int StreamingCalls { get; private set; }
        public string? LastModel { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public IReadOnlyList<ChatCompletionMessage>? LastHistory { get; private set; }
        public IReadOnlyList<ToolAwareMessage>? LastToolMessages { get; private set; }
        public IReadOnlyList<ChatToolDefinition>? LastTools { get; private set; }

        public Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ChatCompletionResult> ChatCompletionAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ChatCompletionMessage> messages,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            PlainCalls++;
            LastModel = model;
            LastSystemPrompt = systemPrompt;
            LastHistory = messages;
            return Task.FromResult(PlainResult);
        }

        public Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            CancellationToken ct = default)
        {
            ToolCalls++;
            LastModel = model;
            LastSystemPrompt = systemPrompt;
            LastToolMessages = messages;
            LastTools = tools;
            return Task.FromResult(ToolResult);
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
            string model,
            string? systemPrompt,
            IReadOnlyList<ToolAwareMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            int? maxCompletionTokens = null,
            Dictionary<string, JsonElement>? providerParameters = null,
            CompletionParameters? completionParameters = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamingCalls++;
            LastModel = model;
            LastSystemPrompt = systemPrompt;
            LastToolMessages = messages;
            LastTools = tools;
            await Task.CompletedTask;

            foreach (var delta in StreamingDeltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return ChatStreamChunk.Text(delta);
            }

            ct.ThrowIfCancellationRequested();
            yield return ChatStreamChunk.Final(StreamingResult);
        }
    }
}
