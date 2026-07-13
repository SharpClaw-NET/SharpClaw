using Microsoft.Extensions.Configuration;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatRuntimePlanningTests
{
    [Test]
    public void HeaderExpansionPlan_IdentifiesRequiredUserAndAgentFacts()
    {
        var planner = new ChatHeaderExpansionPlanner();

        var plan = planner.BuildPlan(
            "{{user}} {{role}} {{agent-grants}} {{agents:{Name}}}",
            Guid.NewGuid(),
            new ChatHeaderExpansionOptions());

        plan.ShouldExpand.Should().BeTrue();
        plan.TagNames.Should().Equal("user", "role", "agent-grants", "agents");
        plan.RequiresUser.Should().BeTrue();
        plan.RequiresUserPermissionSet.Should().BeTrue();
        plan.RequiresAgentPermissionSet.Should().BeTrue();
    }

    [Test]
    public void HeaderExpansionPlan_DisabledExpansionRequiresNoHostFacts()
    {
        var planner = new ChatHeaderExpansionPlanner();

        var plan = planner.BuildPlan(
            "{{user}} {{agent-grants}}",
            Guid.NewGuid(),
            new ChatHeaderExpansionOptions(DisableHeaderTagExpansion: true));

        plan.ShouldExpand.Should().BeFalse();
        plan.TagNames.Should().BeEmpty();
        plan.RequiresUser.Should().BeFalse();
        plan.RequiresUserPermissionSet.Should().BeFalse();
        plan.RequiresAgentPermissionSet.Should().BeFalse();
    }

    [Test]
    public void HeaderExpansionPlan_UserGrantTagsWithoutUserIdRequireNoUserFacts()
    {
        var planner = new ChatHeaderExpansionPlanner();

        var plan = planner.BuildPlan(
            "{{role}} {{grants}}",
            userId: null,
            new ChatHeaderExpansionOptions());

        plan.ShouldExpand.Should().BeTrue();
        plan.RequiresUser.Should().BeFalse();
        plan.RequiresUserPermissionSet.Should().BeFalse();
    }

    [Test]
    public void AgentChangedInvalidation_RemovesOnlyAgentDependentEntries()
    {
        var planner = new ChatRuntimeInvalidationPlanner();
        var cache = CreateCache();
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var otherChannelId = Guid.NewGuid();
        var headerKey = ChatCache.KeyHeaderAgentSuffix(
            agentId,
            channelId,
            "provider",
            null);
        var toolsKey = ChatCache.KeyEffectiveTools(agentId, "all");
        var defaultResourceKey = ChatCache.KeyDefaultResourceResolution(
            channelId,
            agentId,
            "task");
        var unrelatedKey = ChatCache.KeyDefaultResourceResolution(
            otherChannelId,
            otherAgentId,
            "task");

        cache.Set(headerKey, "header");
        cache.Set(toolsKey, "tools");
        cache.Set(defaultResourceKey, "resource");
        cache.Set(unrelatedKey, "unrelated");

        planner.AgentChanged(agentId).ApplyTo(cache);

        cache.TryGet<string>(headerKey, out _).Should().BeFalse();
        cache.TryGet<string>(toolsKey, out _).Should().BeFalse();
        cache.TryGet<string>(defaultResourceKey, out _).Should().BeFalse();
        cache.TryGet<string>(unrelatedKey, out var unrelated).Should().BeTrue();
        unrelated.Should().Be("unrelated");
    }

    [Test]
    public void PermissionSetsChangedInvalidation_RemovesPermissionDerivedPrefixes()
    {
        var planner = new ChatRuntimeInvalidationPlanner();
        var cache = CreateCache();
        var userId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var userKey = ChatCache.KeyHeaderUser(userId);
        var suffixKey = ChatCache.KeyHeaderAgentSuffix(
            agentId,
            channelId,
            "provider",
            "low");
        var defaultResourceKey = ChatCache.KeyDefaultResourceResolution(
            channelId,
            agentId,
            null);
        var jobKey = ChatCache.KeyJobLogs(jobId);

        cache.Set(userKey, "user");
        cache.Set(suffixKey, "suffix");
        cache.Set(defaultResourceKey, "resource");
        cache.Set(jobKey, "job");

        planner.PermissionSetsChanged().ApplyTo(cache);

        cache.TryGet<string>(userKey, out _).Should().BeFalse();
        cache.TryGet<string>(suffixKey, out _).Should().BeFalse();
        cache.TryGet<string>(defaultResourceKey, out _).Should().BeFalse();
        cache.TryGet<string>(jobKey, out var job).Should().BeTrue();
        job.Should().Be("job");
    }

    private static ChatCache CreateCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:CacheMaxBytes"] = "1048576"
            })
            .Build();

        return new ChatCache(configuration);
    }
}
