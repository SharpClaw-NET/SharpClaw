using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Triggers;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class TaskTriggerBindingPlannerTests
{
    [Test]
    public void BuildSyncPlan_CreatesDefaultBindingWithSourceValueAndFilter()
    {
        var source = new TestTriggerSource("webhook");
        var registry = new TestTriggerSourceRegistry([source]);
        var planner = new TaskTriggerBindingPlanner();
        var definition = new TaskDefinitionDescriptor(Guid.NewGuid(), "Task");
        var trigger = Trigger("webhook", ("path", "/hook"), ("filter", "POST"));

        var plan = planner.BuildSyncPlan(new(
            definition,
            [trigger],
            [],
            registry));

        plan.OwnedSourceSyncs.Should().BeEmpty();
        plan.DefaultBindingsToRemove.Should().BeEmpty();
        plan.DefaultBindingsToCreate.Should().ContainSingle();
        var creation = plan.DefaultBindingsToCreate.Single();
        creation.TaskDefinitionId.Should().Be(definition.Id);
        creation.Kind.Should().Be("webhook");
        creation.TriggerValue.Should().Be("/hook");
        creation.Filter.Should().Be("POST");
        creation.DefinitionJson.Should().Contain("\"TriggerKey\":\"webhook\"");
        creation.Trigger.Should().BeSameAs(trigger);
    }

    [Test]
    public void BuildSyncPlan_RemovesStaleDefaultBindings()
    {
        var source = new TestTriggerSource("webhook");
        var registry = new TestTriggerSourceRegistry([source]);
        var planner = new TaskTriggerBindingPlanner();
        var definition = new TaskDefinitionDescriptor(Guid.NewGuid(), "Task");
        var current = Trigger("webhook", ("path", "/current"));
        var stale = new TaskTriggerBindingSnapshot(
            definition.Id,
            "webhook",
            "/old",
            null);

        var plan = planner.BuildSyncPlan(new(
            definition,
            [current],
            [stale],
            registry));

        plan.DefaultBindingsToRemove.Should().ContainSingle()
            .Which.Should().Be(stale);
        plan.DefaultBindingsToCreate.Should().ContainSingle()
            .Which.TriggerValue.Should().Be("/current");
    }

    [Test]
    public void BuildSyncPlan_GroupsOwnedSourceTriggersAndSkipsDefaultRows()
    {
        var owned = new TestTriggerSource("scheduled", ownsPersistence: true);
        var defaultSource = new TestTriggerSource("webhook");
        var registry = new TestTriggerSourceRegistry([owned, defaultSource]);
        var planner = new TaskTriggerBindingPlanner();
        var definition = new TaskDefinitionDescriptor(Guid.NewGuid(), "Task");
        var scheduled = Trigger("scheduled", ("name", "daily"));
        var webhook = Trigger("webhook", ("path", "/hook"));

        var plan = planner.BuildSyncPlan(new(
            definition,
            [scheduled, webhook],
            [],
            registry));

        plan.OwnedSourceSyncs.Should().ContainSingle();
        plan.OwnedSourceSyncs.Single().Source.Should().BeSameAs(owned);
        plan.OwnedSourceSyncs.Single().Triggers.Should().ContainSingle()
            .Which.Should().BeSameAs(scheduled);
        plan.DefaultBindingsToCreate.Should().ContainSingle()
            .Which.Kind.Should().Be("webhook");
    }

    private static TaskTriggerDefinition Trigger(
        string key,
        params (string Key, string? Value)[] parameters) =>
        new()
        {
            TriggerKey = key,
            Parameters = parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal)
        };

    private sealed class TestTriggerSource(
        string key,
        bool ownsPersistence = false) : ITaskTriggerSource
    {
        public string? TriggerKey => key;

        public bool OwnsBindingPersistence => ownsPersistence;

        public string? GetBindingValue(TaskTriggerDefinition def) =>
            def.Parameters.TryGetValue("path", out var value)
                ? value
                : def.Parameters.TryGetValue("name", out var name)
                    ? name
                    : null;

        public string? GetBindingFilter(TaskTriggerDefinition def) =>
            def.Parameters.TryGetValue("filter", out var value)
                ? value
                : null;

        public Task StartAsync(
            IReadOnlyList<ITaskTriggerSourceContext> contexts,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class TestTriggerSourceRegistry(
        IReadOnlyList<ITaskTriggerSource> sources)
        : ITaskTriggerSourceRegistry
    {
        public IReadOnlyList<ITaskTriggerSource> Sources { get; } = sources;

        public IReadOnlyList<ITaskTriggerBindingSideEffect> SideEffects { get; }
            = [];

        public ITaskTriggerSource? ResolveByKey(string? triggerKey) =>
            Sources.FirstOrDefault(source =>
                source.TriggerKeys.Contains(
                    triggerKey,
                    StringComparer.Ordinal));

        public ITaskTriggerBindingSideEffect? ResolveSideEffect(
            string? triggerKey) =>
            null;
    }
}
