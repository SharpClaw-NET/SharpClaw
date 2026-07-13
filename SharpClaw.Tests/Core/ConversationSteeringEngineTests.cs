using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Conversation;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ConversationSteeringEngineTests
{
    private readonly ConversationSteeringEngine _engine = new();

    [Test]
    public void PrepareAdd_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var act = () => _engine.PrepareAdd(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("request");
    }

    [Test]
    public void PrepareAdd_NormalizesTextClientTypeAndFormatsContent()
    {
        var channelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var threadId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var prepared = _engine.PrepareAdd(new ConversationSteeringRequest(
            channelId,
            threadId,
            "  retry the build  ",
            Source: " module_dev ",
            Category: " build ",
            Details: "  keep it scoped  ",
            ClientType: " worker "));

        prepared.ChannelId.Should().Be(channelId);
        prepared.ThreadId.Should().Be(threadId);
        prepared.Role.Should().Be(ChatRoles.System);
        prepared.Origin.Should().Be(MessageOrigin.System);
        prepared.ClientType.Should().Be("worker");
        prepared.Source.Should().Be("module_dev");
        prepared.Category.Should().Be("build");
        prepared.Content.Should().Be(string.Join(
            Environment.NewLine,
            ConversationSteeringEngine.ContentPrefix,
            "Source: module_dev",
            "Category: build",
            "Summary:",
            "retry the build",
            "Details:",
            "keep it scoped"));

        var metadata = _engine.ParseMetadata(prepared.ProviderMetadataJson);
        metadata.Should().NotBeNull();
        metadata!.Kind.Should().Be(ConversationSteeringEngine.MetadataKind);
        metadata.Source.Should().Be("module_dev");
        metadata.Category.Should().Be("build");
    }

    [Test]
    public void PrepareAdd_WhenOptionalFieldsAreWhitespace_UsesNullMetadataAndDefaultClientType()
    {
        var prepared = _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            "summary",
            Source: " ",
            Category: "\t",
            Details: "",
            ClientType: " "));

        prepared.ClientType.Should().Be(WellKnownClientKeys.Api);
        prepared.Source.Should().BeNull();
        prepared.Category.Should().BeNull();
        prepared.Content.Should().Be(string.Join(
            Environment.NewLine,
            ConversationSteeringEngine.ContentPrefix,
            "Summary:",
            "summary"));
        prepared.Content.Should().NotContain("Source:");
        prepared.Content.Should().NotContain("Category:");
        prepared.Content.Should().NotContain("Details:");
    }

    [Test]
    public void PrepareAdd_EnforcesBoundaryLengths()
    {
        var valid = _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            new string('s', ConversationSteeringEngine.MaxSummaryCharacters),
            Source: new string('a', ConversationSteeringEngine.MaxMetadataCharacters),
            Category: new string('b', ConversationSteeringEngine.MaxMetadataCharacters),
            Details: new string('d', ConversationSteeringEngine.MaxDetailsCharacters)));

        valid.Content.Should().Contain(new string('s', ConversationSteeringEngine.MaxSummaryCharacters));

        var blankSummary = () => _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            " "));
        var longSummary = () => _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            new string('s', ConversationSteeringEngine.MaxSummaryCharacters + 1)));
        var longDetails = () => _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            "summary",
            Details: new string('d', ConversationSteeringEngine.MaxDetailsCharacters + 1)));
        var longSource = () => _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            "summary",
            Source: new string('x', ConversationSteeringEngine.MaxMetadataCharacters + 1)));
        var longCategory = () => _engine.PrepareAdd(new ConversationSteeringRequest(
            Guid.NewGuid(),
            ThreadId: null,
            "summary",
            Category: new string('x', ConversationSteeringEngine.MaxMetadataCharacters + 1)));

        blankSummary.Should().Throw<ArgumentException>()
            .WithMessage("Summary is required. (Parameter 'Summary')");
        longSummary.Should().Throw<ArgumentException>()
            .WithMessage("Summary must be 8000 characters or less. (Parameter 'Summary')");
        longDetails.Should().Throw<ArgumentException>()
            .WithMessage("Value must be 16000 characters or less. (Parameter 'value')");
        longSource.Should().Throw<ArgumentException>()
            .WithMessage("Value must be 128 characters or less. (Parameter 'value')");
        longCategory.Should().Throw<ArgumentException>()
            .WithMessage("Value must be 128 characters or less. (Parameter 'value')");
    }

    [Test]
    public void MetadataParsing_ReturnsNullForBlankInvalidOrWrongKind()
    {
        _engine.ParseMetadata(null).Should().BeNull();
        _engine.ParseMetadata(" ").Should().BeNull();
        _engine.ParseMetadata("{not-json").Should().BeNull();
        _engine.ParseMetadata("""{"kind":"other","source":"s","category":"c"}""")
            .Should().BeNull();

        var parsed = _engine.ParseMetadata(
            """{"kind":"sharpclaw.conversation_steering","source":"s","category":"c"}""");

        parsed.Should().NotBeNull();
        parsed!.Source.Should().Be("s");
        parsed.Category.Should().Be("c");
    }

    [Test]
    public void LimitAndPrefixHelpers_PreserveSteeringListRules()
    {
        _engine.ClampListLimit(0).Should().Be(1);
        _engine.ClampListLimit(20).Should().Be(20);
        _engine.ClampListLimit(500).Should().Be(100);
        _engine.IsSteeringMessage(ChatRoles.System, ConversationSteeringEngine.ContentPrefix + " text")
            .Should().BeTrue();
        _engine.IsSteeringMessage("assistant", ConversationSteeringEngine.ContentPrefix + " text")
            .Should().BeFalse();
        _engine.IsSteeringMessage(ChatRoles.System, "not steering")
            .Should().BeFalse();
        _engine.IsSteeringMessage(ChatRoles.System, null)
            .Should().BeFalse();
    }

    [Test]
    public void EnsureTargetValid_PreservesHostValidationMessages()
    {
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var threadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var actualChannelId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var emptyChannel = () => _engine.EnsureTargetValid(
            Guid.Empty,
            null,
            channelExists: true,
            threadChannelId: null);
        var missingChannel = () => _engine.EnsureTargetValid(
            channelId,
            null,
            channelExists: false,
            threadChannelId: null);
        var missingThread = () => _engine.EnsureTargetValid(
            channelId,
            threadId,
            channelExists: false,
            threadChannelId: null);
        var wrongThreadChannel = () => _engine.EnsureTargetValid(
            channelId,
            threadId,
            channelExists: false,
            actualChannelId);

        emptyChannel.Should().Throw<ArgumentException>()
            .WithMessage("channelId is required. (Parameter 'channelId')");
        missingChannel.Should().Throw<InvalidOperationException>()
            .WithMessage($"Channel '{channelId}' was not found.");
        missingThread.Should().Throw<InvalidOperationException>()
            .WithMessage($"Thread '{threadId}' was not found.");
        wrongThreadChannel.Should().Throw<InvalidOperationException>()
            .WithMessage($"Thread '{threadId}' belongs to channel '{actualChannelId}', not '{channelId}'.");
    }

    [Test]
    public void ToListResponse_ProjectsMetadataAndOrdersAscendingByTimestampThenId()
    {
        var channelId = Guid.NewGuid();
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var timestamp = DateTimeOffset.Parse("2026-07-02T18:00:00Z");
        var metadata = _engine.PrepareAdd(new ConversationSteeringRequest(
            channelId,
            null,
            "summary",
            Source: "source",
            Category: "category")).ProviderMetadataJson;

        var responses = _engine.ToListResponse(
        [
            new ConversationSteeringStoredMessage(
                thirdId,
                channelId,
                null,
                "third",
                timestamp.AddMinutes(1),
                "{not-json"),
            new ConversationSteeringStoredMessage(
                secondId,
                channelId,
                null,
                "second",
                timestamp,
                metadata),
            new ConversationSteeringStoredMessage(
                firstId,
                channelId,
                null,
                "first",
                timestamp,
                """{"kind":"other"}"""),
        ]);

        responses.Select(response => response.MessageId).Should()
            .Equal(firstId, secondId, thirdId);
        responses[0].Source.Should().BeNull();
        responses[1].Source.Should().Be("source");
        responses[1].Category.Should().Be("category");
        responses[2].Source.Should().BeNull();
    }
}
