using System.Text.Json;
using SharpClaw.Application.Services;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public class TaskSharedDataStoreTests
{
    [Test]
    public void RegisterBuiltInTools_RegistersExpectedTaskTools()
    {
        var store = new TaskSharedDataStore();

        store.RegisterBuiltInTools();
        var toolNames = store.GetToolDefinitions().Select(t => t.Name).ToList();

        toolNames.Should().Contain("task_write_light_data");
        toolNames.Should().Contain("task_read_light_data");
        toolNames.Should().Contain("task_write_big_data");
        toolNames.Should().Contain("task_read_big_data");
        toolNames.Should().Contain("task_list_big_data");
        toolNames.Should().Contain("task_view_info");
        toolNames.Should().Contain("task_view_source");
        toolNames.Should().NotContain("task_output");
    }

    [Test]
    public void RegisterBuiltInTools_WithAllowedOutputFormat_IncludesTaskOutput()
    {
        var store = new TaskSharedDataStore
        {
            AllowedOutputFormat = "json"
        };

        store.RegisterBuiltInTools();

        store.GetToolDefinitions().Select(t => t.Name).Should().Contain("task_output");
    }

    [Test]
    public async Task TryInvokeToolAsync_TaskWriteBigData_RejectsOversizedContent()
    {
        var store = new TaskSharedDataStore();
        store.RegisterBuiltInTools();

        var args = JsonDocument.Parse($"{{\"title\":\"Large\",\"content\":\"{new string('x', TaskSharedDataStore.MaxBigDataCharacters + 1)}\"}}").RootElement;
        var result = await store.TryInvokeToolAsync("task_write_big_data", args, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Result.Should().Contain("character limit");
        store.BigData.Should().BeEmpty();
    }

    [Test]
    public async Task TryInvokeToolAsync_CustomToolHook_UsesRegisteredDescriptor()
    {
        var store = new TaskSharedDataStore();
        var hook = new SharpClaw.Application.Infrastructure.Tasks.Models.TaskToolCallHook
        {
            Name = "custom_echo",
            Description = "Echo value",
            Parameters = [new("value", "string", null)],
            Body = [],
            ReturnVariable = null
        };
        store.RegisterCustomToolHook(hook, (args, ct) => Task.FromResult(args?.GetProperty("value").GetString() ?? string.Empty));

        var args = JsonDocument.Parse("{\"value\":\"hello\"}").RootElement;
        var result = await store.TryInvokeToolAsync("custom_echo", args, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Result.Should().Be("hello");
        store.GetToolDefinitions().Select(t => t.Name).Should().Contain("custom_echo");
    }
}
