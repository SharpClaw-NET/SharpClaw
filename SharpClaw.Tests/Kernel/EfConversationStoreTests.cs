using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class EfConversationStoreTests
{
    [Test]
    public async Task Store_commits_and_reloads_direct_chat_history_with_metadata_and_usage()
    {
        var databaseName = $"kernel-chat-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IRuntimePersistenceActionBoundary, DirectPersistenceActionBoundary>();
        services.AddScoped<IRuntimePersistenceActionRunnerAccessor, RuntimePersistenceActionRunnerAccessor>();
        services.AddScoped<RuntimePersistenceActionRunner>();
        services.AddSingleton<EfConversationStore>();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<EfConversationStore>();
        var conversationId = Guid.NewGuid();
        var turn = new ChatTurnContext(
            Guid.NewGuid(),
            new ChatTurnInput("hello", conversationId, ClientType: "test"),
            new ConversationSelection(conversationId));

        await store.CommitExchangeAsync(
            new ChatExchange(
                turn,
                "hello",
                new ChatCompletionResult
                {
                    Content = "reply",
                    FinishReason = FinishReason.Stop,
                    ProviderMetadataJson = "{\"provider\":\"test\"}",
                    Usage = new TokenUsage(2, 3),
                }),
            CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var rows = await db.ChatMessages.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(2);
            rows.Select(row => row.ChannelId).Should().AllBeEquivalentTo(conversationId);
            rows.Select(row => row.ThreadId).Should().AllSatisfy(value => value.Should().BeNull());
        }

        var history = await store.LoadHistoryAsync(conversationId, CancellationToken.None);

        history.Should().HaveCount(2);
        history[0].Role.Should().Be("user");
        history[0].Content.Should().Be("hello");
        history[1].Role.Should().Be("assistant");
        history[1].Content.Should().Be("reply");
        history[1].ProviderMetadataJson.Should().Be("{\"provider\":\"test\"}");
    }

    private sealed class DirectPersistenceActionBoundary : IRuntimePersistenceActionBoundary
    {
        public async ValueTask RunPersistenceActionAsync(
            RuntimePersistenceActionInvocation invocation,
            Func<CancellationToken, ValueTask<int>> terminal,
            CancellationToken cancellationToken = default)
        {
            invocation.ActionKey.Value.Should().Be("storage.upsert.commit");
            _ = await terminal(cancellationToken);
        }
    }
}
