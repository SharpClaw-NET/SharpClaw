using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatHistoryEngineTests
{
    private readonly ChatHistoryEngine _engine = new();

    [Test]
    public void ResolveLimits_WhenThreadLimitsAreNull_UsesCoreDefaults()
    {
        var limits = _engine.ResolveLimits(null, null);

        limits.MaxMessages.Should().Be(ChatHistoryEngine.DefaultMaxMessages);
        limits.MaxCharacters.Should().Be(ChatHistoryEngine.DefaultMaxCharacters);
    }

    [Test]
    public void BuildProviderHistory_WhenMoreThanMaxMessages_KeepsNewestMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var history = _engine.BuildProviderHistory(
            [
                Message(now.AddSeconds(3), "third"),
                Message(now.AddSeconds(1), "first"),
                Message(now.AddSeconds(2), "second")
            ],
            new ChatHistoryLimits(MaxMessages: 2, MaxCharacters: 100));

        history.Select(m => m.Content).Should().Equal("second", "third");
    }

    [Test]
    public void BuildProviderHistory_WhenCharacterLimitIsExceeded_TrimsOldestUntilItFits()
    {
        var now = DateTimeOffset.UtcNow;
        var history = _engine.BuildProviderHistory(
            [
                Message(now.AddSeconds(1), "aaaa"),
                Message(now.AddSeconds(2), "bbbb"),
                Message(now.AddSeconds(3), "cc")
            ],
            new ChatHistoryLimits(MaxMessages: 10, MaxCharacters: 6));

        history.Select(m => m.Content).Should().Equal("bbbb", "cc");
    }

    [Test]
    public void BuildProviderHistory_WhenMetadataExists_PreservesProviderMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var history = _engine.BuildProviderHistory(
            [Message(now, "hello", metadata: """{"id":"provider-state"}""")],
            new ChatHistoryLimits(MaxMessages: 10, MaxCharacters: 100));

        history.Should().ContainSingle();
        history[0].ProviderMetadataJson.Should().Be("""{"id":"provider-state"}""");
    }

    private static ChatHistoryMessage Message(
        DateTimeOffset createdAt,
        string content,
        string role = "user",
        string? metadata = null)
    {
        return new ChatHistoryMessage(
            createdAt,
            role,
            content,
            metadata);
    }
}
