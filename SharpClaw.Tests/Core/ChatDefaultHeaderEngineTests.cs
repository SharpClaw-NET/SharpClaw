using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Clients;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatDefaultHeaderEngineTests
{
    [Test]
    public void IsHeaderDisabled_WhenChannelOrContextDisablesHeader_ReturnsTrue()
    {
        var engine = CreateEngine();

        engine.IsHeaderDisabled(new ChannelDB
        {
            Title = "Direct",
            DisableChatHeader = true
        }).Should().BeTrue();

        engine.IsHeaderDisabled(new ChannelDB
        {
            Title = "Inherited",
            AgentContext = new ChannelContextDB
            {
                Name = "Context",
                DisableChatHeader = true
            }
        }).Should().BeTrue();

        engine.IsHeaderDisabled(new ChannelDB { Title = "Enabled" })
            .Should().BeFalse();
    }

    [Test]
    public void ResolveCustomTemplate_WhenChannelTemplateExists_UsesChannelBeforeAgent()
    {
        var engine = CreateEngine();
        var template = engine.ResolveCustomTemplate(
            new ChannelDB
            {
                Title = "Channel",
                CustomChatHeader = "channel-template"
            },
            new AgentDB
            {
                Name = "Agent",
                CustomChatHeader = "agent-template"
            });

        template.Should().Be("channel-template");
    }

    [Test]
    public void BuildTaskHeader_WhenSharedDataExists_FormatsTaskFactsAndSuffix()
    {
        var engine = CreateEngine();
        var header = engine.BuildTaskHeader(
            new ChatTaskHeaderFacts(
                "Nightly Sync",
                "sync state",
                [new ChatTaskBigDataReference("memo", "Long plan")]),
            " | agent-role: Worker]" + Environment.NewLine,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        header.Should().Be(
            "[time: 2026-01-02 03:04:05 UTC | source: automated task " +
            "| task: Nightly Sync | shared-data: sync state " +
            "| big-data-ids: [memo:\"Long plan\"] | agent-role: Worker]" +
            Environment.NewLine);
    }

    [Test]
    public void BuildAuthenticatedUserHeader_WhenRoleAndBioExist_FormatsUserFacts()
    {
        var engine = CreateEngine();
        var header = engine.BuildAuthenticatedUserHeader(
            new ChatAuthenticatedUserHeaderFacts(
                "marko",
                "api",
                "Operator",
                ["ReadLogs", "Documents[aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa]"],
                "supervises agents"),
            " | agent-role: Worker]" + Environment.NewLine,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        header.Should().Be(
            "[time: 2026-01-02 03:04:05 UTC | user: marko | via: api " +
            "| role: Operator (ReadLogs, Documents[aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa]) " +
            "| bio: supervises agents | agent-role: Worker]" +
            Environment.NewLine);
    }

    [Test]
    public void BuildExternalUserHeader_WhenDisplayNameDiffers_AddsUsernameHandle()
    {
        var engine = CreateEngine();
        var header = engine.BuildExternalUserHeader(
            new ChatExternalUserHeaderFacts("mkn8rn", "Marko", "discord"),
            " | agent-role: Worker]" + Environment.NewLine,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        header.Should().Be(
            "[time: 2026-01-02 03:04:05 UTC | user: Marko (@mkn8rn) " +
            "| via: discord | agent-role: Worker]" +
            Environment.NewLine);
    }

    [Test]
    public void BuildAgentSuffix_WhenProviderTreatsReasoningEffortAsInformational_AddsNotice()
    {
        var engine = CreateEngine(new Plugin(
            "local",
            new InformationalReasoningSpec()));
        var suffix = engine.BuildAgentSuffix(
            new ChatAgentHeaderSuffixFacts(
                "Worker",
                ["ReadLogs"]),
            new CompletionParameters { ReasoningEffort = "HIGH" },
            "local");

        suffix.Should().Be(
            " | agent-role: Worker (ReadLogs) " +
            "| policy: unlisted-resource/GUID=denied; disclose gaps to user " +
            "| reasoning-effort: high " +
            "(informational; this model has no mechanical reasoning-effort control)]" +
            Environment.NewLine);
    }

    private static ChatDefaultHeaderEngine CreateEngine(params IProviderPlugin[] plugins) =>
        new(new ProviderApiClientFactory(plugins));

    private sealed record Plugin(
        string ProviderKey,
        ICompletionParameterSpec ParameterSpec) : IProviderPlugin
    {
        public string DisplayName => ProviderKey;
        public bool RequiresEndpoint => false;
        public IProviderApiClient CreateClient(string? endpoint) =>
            throw new NotSupportedException();
        public IModelCapabilityResolver Capabilities { get; } = new Capabilities();
        public IReadOnlyList<ProviderCostSeed> CostSeeds => [];
        public IDeviceCodeFlow? DeviceCodeFlow => null;
    }

    private sealed class Capabilities : IModelCapabilityResolver
    {
        public HashSet<string> Resolve(string modelName) => [];
    }

    private sealed class InformationalReasoningSpec : ICompletionParameterSpec
    {
        private static readonly ICompletionParameterSpec Inner =
            ICompletionParameterSpec.Passthrough;

        public string ProviderName => "Local";
        public bool SupportsTemperature => Inner.SupportsTemperature;
        public float TemperatureMin => Inner.TemperatureMin;
        public float TemperatureMax => Inner.TemperatureMax;
        public bool SupportsTopP => Inner.SupportsTopP;
        public float TopPMin => Inner.TopPMin;
        public float TopPMax => Inner.TopPMax;
        public bool SupportsTopK => Inner.SupportsTopK;
        public int TopKMin => Inner.TopKMin;
        public int TopKMax => Inner.TopKMax;
        public bool SupportsFrequencyPenalty => Inner.SupportsFrequencyPenalty;
        public float FrequencyPenaltyMin => Inner.FrequencyPenaltyMin;
        public float FrequencyPenaltyMax => Inner.FrequencyPenaltyMax;
        public bool SupportsPresencePenalty => Inner.SupportsPresencePenalty;
        public float PresencePenaltyMin => Inner.PresencePenaltyMin;
        public float PresencePenaltyMax => Inner.PresencePenaltyMax;
        public bool SupportsStop => Inner.SupportsStop;
        public int MaxStopSequences => Inner.MaxStopSequences;
        public bool SupportsSeed => Inner.SupportsSeed;
        public bool SupportsResponseFormat => Inner.SupportsResponseFormat;
        public bool RejectsJsonObjectResponseFormat => Inner.RejectsJsonObjectResponseFormat;
        public bool OnlyJsonObjectResponseFormat => Inner.OnlyJsonObjectResponseFormat;
        public bool SupportsReasoningEffort => Inner.SupportsReasoningEffort;
        public bool ReasoningEffortInformationalOnly => true;
        public string[] ValidReasoningEffortValues => Inner.ValidReasoningEffortValues;
        public bool SupportsToolChoice => Inner.SupportsToolChoice;
        public bool SupportsStrictTools => Inner.SupportsStrictTools;
    }

}
