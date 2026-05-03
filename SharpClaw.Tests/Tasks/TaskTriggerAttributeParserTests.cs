using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.ComputerUse.Triggers;
using SharpClaw.Modules.DatabaseAccess.Triggers;
using SharpClaw.Modules.FilesystemTriggers;
using SharpClaw.Modules.Http;
using SharpClaw.Modules.Metrics;
using SharpClaw.Modules.NetworkTriggers;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Parser tests for trigger-attribute extraction. After the
/// trigger-extraction cleanup the parser no longer populates
/// typed properties on <see cref="TaskTriggerDefinition"/>; module
/// handlers write directly into the opaque
/// <see cref="TaskTriggerDefinition.Parameters"/> map. These tests
/// assert that surface.
/// </summary>
[TestFixture]
public class TaskTriggerAttributeParserTests
{
    private static TaskScriptDefinition ParseOk(string source)
    {
        var result = TaskScriptEngine.Parse(source);
        result.Diagnostics.Where(d => d.Severity == TaskDiagnosticSeverity.Error)
            .Should().BeEmpty("source should parse without errors");
        return result.Definition!;
    }

    private static string Wrap(string classBody, string attributes = "") => $$"""
{{attributes}}
[Task("TriggerTask")]
public class TriggerTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("ok");
    }
{{classBody}}
}
""";

    private static TaskTriggerDefinition Single(string source) =>
        ParseOk(source).TriggerDefinitions.Should().ContainSingle().Subject;

    [Test]
    public void Parse_NoTriggerAttributes_ReturnsEmptyTriggerDefinitions()
    {
        ParseOk(Wrap("")).TriggerDefinitions.Should().BeEmpty();
    }

    // ── [Schedule] ────────────────────────────────────────────────

    [Test]
    public void Parse_Schedule_PopulatesCronTriggerKeyAndExpression()
    {
        var t = Single(Wrap("", """[Schedule("0 9 * * MON-FRI")]"""));
        t.TriggerKey.Should().Be(TaskScriptingTriggerKeys.Cron);
        t.Parameters[TaskScriptingTriggerKeys.CronExpression].Should().Be("0 9 * * MON-FRI");
        t.Parameters.ContainsKey(TaskScriptingTriggerKeys.CronTimezone).Should().BeFalse();
    }

    [Test]
    public void Parse_ScheduleWithTimezone_ExtractsCronTimezone()
    {
        var t = Single(Wrap("", """[Schedule("0 9 * * *", Timezone = "America/New_York")]"""));
        t.TriggerKey.Should().Be(TaskScriptingTriggerKeys.Cron);
        t.Parameters[TaskScriptingTriggerKeys.CronExpression].Should().Be("0 9 * * *");
        t.Parameters[TaskScriptingTriggerKeys.CronTimezone].Should().Be("America/New_York");
    }

    // ── [OnEvent] ─────────────────────────────────────────────────

    [Test]
    public void Parse_OnEvent_PopulatesEventTriggerKeyAndType()
    {
        var t = Single(Wrap("", """[OnEvent("ModelAdded")]"""));
        t.TriggerKey.Should().Be(AgentOrchestrationTriggerKeys.Event);
        t.Parameters[AgentOrchestrationTriggerKeys.EventType].Should().Be("ModelAdded");
    }

    [Test]
    public void Parse_OnEventWithFilter_ExtractsFilter()
    {
        var t = Single(Wrap("", """[OnEvent("ModelAdded", Filter = "provider=openai")]"""));
        t.Parameters[AgentOrchestrationTriggerKeys.EventType].Should().Be("ModelAdded");
        t.Parameters[AgentOrchestrationTriggerKeys.EventFilter].Should().Be("provider=openai");
    }

    // ── [OnFileChanged] ───────────────────────────────────────────

    [Test]
    public void Parse_OnFileChanged_PopulatesFileChangedTriggerKeyAndPath()
    {
        var t = Single(Wrap("", """[OnFileChanged("/tmp/data")]"""));
        t.TriggerKey.Should().Be(FilesystemTriggerKeys.FileChanged);
        t.Parameters[FilesystemTriggerKeys.WatchPath].Should().Be("/tmp/data");
        t.Parameters[FilesystemTriggerKeys.FileEvents].Should().Be(FileWatchEvent.Any.ToString());
    }

    [Test]
    public void Parse_OnFileChangedWithPatternAndEvents_ExtractsNamedArgs()
    {
        var t = Single(Wrap("", """[OnFileChanged("/tmp/data", Pattern = "*.json", Events = FileWatchEvent.Created | FileWatchEvent.Deleted)]"""));
        t.Parameters[FilesystemTriggerKeys.FilePattern].Should().Be("*.json");
        t.Parameters[FilesystemTriggerKeys.FileEvents].Should().Be((FileWatchEvent.Created | FileWatchEvent.Deleted).ToString());
    }

    // ── [OnProcessStarted] / [OnProcessStopped] ───────────────────

    [Test]
    public void Parse_OnProcessStarted_PopulatesProcessStartedTriggerKey()
    {
        var t = Single(Wrap("", """[OnProcessStarted("chrome")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.ProcessStarted);
        t.Parameters[ComputerUseTriggerKeys.ProcessName].Should().Be("chrome");
    }

    [Test]
    public void Parse_OnProcessStopped_PopulatesProcessStoppedTriggerKey()
    {
        var t = Single(Wrap("", """[OnProcessStopped("chrome")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.ProcessStopped);
        t.Parameters[ComputerUseTriggerKeys.ProcessName].Should().Be("chrome");
    }

    // ── [OnWebhook] ───────────────────────────────────────────────

    [Test]
    public void Parse_OnWebhook_PopulatesWebhookTriggerKeyAndRoute()
    {
        var t = Single(Wrap("", """[OnWebhook("/hook/deploy")]"""));
        t.TriggerKey.Should().Be(HttpTriggerKeys.Webhook);
        t.Parameters[HttpTriggerKeys.WebhookRoute].Should().Be("/hook/deploy");
    }

    [Test]
    public void Parse_OnWebhookWithSecret_ExtractsSecretAndSignatureHeader()
    {
        var t = Single(Wrap("", """[OnWebhook("/hook/deploy", Secret = "GH_SECRET", SignatureHeader = "X-Hub-Signature-256")]"""));
        t.Parameters[HttpTriggerKeys.WebhookSecretEnvVar].Should().Be("GH_SECRET");
        t.Parameters[HttpTriggerKeys.WebhookSignatureHeader].Should().Be("X-Hub-Signature-256");
    }

    // ── [OnHostReachable] / [OnHostUnreachable] ───────────────────

    [Test]
    public void Parse_OnHostReachable_PopulatesHostReachableTriggerKeyAndName()
    {
        var t = Single(Wrap("", """[OnHostReachable("db.internal")]"""));
        t.TriggerKey.Should().Be(NetworkTriggerKeys.HostReachable);
        t.Parameters[NetworkTriggerKeys.HostName].Should().Be("db.internal");
    }

    [Test]
    public void Parse_OnHostUnreachableWithPort_ExtractsPort()
    {
        var t = Single(Wrap("", """[OnHostUnreachable("db.internal", Port = 5432)]"""));
        t.TriggerKey.Should().Be(NetworkTriggerKeys.HostUnreachable);
        t.Parameters[NetworkTriggerKeys.HostPort].Should().Be("5432");
    }

    // ── [OnTaskCompleted] / [OnTaskFailed] ────────────────────────

    [Test]
    public void Parse_OnTaskCompleted_PopulatesSourceTaskName()
    {
        var t = Single(Wrap("", """[OnTaskCompleted("IngestData")]"""));
        t.TriggerKey.Should().Be(TaskScriptingTriggerKeys.TaskCompleted);
        t.Parameters[TaskScriptingTriggerKeys.SourceTaskName].Should().Be("IngestData");
    }

    [Test]
    public void Parse_OnTaskFailed_PopulatesSourceTaskName()
    {
        var t = Single(Wrap("", """[OnTaskFailed("IngestData")]"""));
        t.TriggerKey.Should().Be(TaskScriptingTriggerKeys.TaskFailed);
        t.Parameters[TaskScriptingTriggerKeys.SourceTaskName].Should().Be("IngestData");
    }

    // ── [OnWindowFocused] / [OnWindowBlurred] ─────────────────────

    [Test]
    public void Parse_OnWindowFocused_PopulatesWindowFocusedTriggerKey()
    {
        var t = Single(Wrap("", """[OnWindowFocused("notepad")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.WindowFocused);
        t.Parameters[ComputerUseTriggerKeys.ProcessName].Should().Be("notepad");
    }

    [Test]
    public void Parse_OnWindowBlurred_PopulatesWindowBlurredTriggerKey()
    {
        var t = Single(Wrap("", """[OnWindowBlurred("notepad")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.WindowBlurred);
        t.Parameters[ComputerUseTriggerKeys.ProcessName].Should().Be("notepad");
    }

    // ── [OnHotkey] ────────────────────────────────────────────────

    [Test]
    public void Parse_OnHotkeyValidCombo_PopulatesHotkeyTriggerKey()
    {
        var t = Single(Wrap("", """[OnHotkey("Ctrl+Shift+F10")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.Hotkey);
        t.Parameters[ComputerUseTriggerKeys.HotkeyCombo].Should().Be("Ctrl+Shift+F10");
    }

    [Test]
    public void Parse_OnHotkeyInvalidCombo_EmitsTask429()
    {
        var result = TaskScriptEngine.Parse(Wrap("", """[OnHotkey("BadCombo")]"""));
        result.Diagnostics.Should().Contain(d => d.Code == "TASK429");
    }

    [Test]
    public void Parse_OnHotkeyEmptyString_EmitsTask429()
    {
        var result = TaskScriptEngine.Parse(Wrap("", """[OnHotkey("")]"""));
        result.Diagnostics.Should().Contain(d => d.Code == "TASK429");
    }

    // ── [OnSystemIdle] / [OnSystemActive] ─────────────────────────

    [Test]
    public void Parse_OnSystemIdle_PopulatesIdleMinutesWithSystemIdleTriggerKey()
    {
        var t = Single(Wrap("", """[OnSystemIdle(Minutes = 15)]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.SystemIdle);
        t.Parameters[ComputerUseTriggerKeys.IdleMinutes].Should().Be("15");
    }

    [Test]
    public void Parse_OnSystemActive_PopulatesSystemActiveTriggerKey()
    {
        Single(Wrap("", """[OnSystemActive]""")).TriggerKey
            .Should().Be(ComputerUseTriggerKeys.SystemActive);
    }

    // ── [OnScreenLocked] / [OnScreenUnlocked] ─────────────────────

    [Test]
    public void Parse_OnScreenLocked_PopulatesScreenLockedTriggerKey()
    {
        Single(Wrap("", """[OnScreenLocked]""")).TriggerKey
            .Should().Be(ComputerUseTriggerKeys.ScreenLocked);
    }

    [Test]
    public void Parse_OnScreenUnlocked_PopulatesScreenUnlockedTriggerKey()
    {
        Single(Wrap("", """[OnScreenUnlocked]""")).TriggerKey
            .Should().Be(ComputerUseTriggerKeys.ScreenUnlocked);
    }

    // ── [OnNetworkChanged] ────────────────────────────────────────

    [Test]
    public void Parse_OnNetworkChanged_PopulatesNetworkChangedTriggerKey()
    {
        Single(Wrap("", """[OnNetworkChanged]""")).TriggerKey
            .Should().Be(NetworkTriggerKeys.NetworkChanged);
    }

    [Test]
    public void Parse_OnNetworkChangedWithSsidAndState_ExtractsNamedArgs()
    {
        var t = Single(Wrap("", """[OnNetworkChanged(Ssid = "HomeNet", State = NetworkState.Connected)]"""));
        t.Parameters[NetworkTriggerKeys.NetworkSsid].Should().Be("HomeNet");
        t.Parameters[NetworkTriggerKeys.NetworkState].Should().Be(NetworkState.Connected.ToString());
    }

    // ── [OnDeviceConnected] / [OnDeviceDisconnected] ──────────────

    [Test]
    public void Parse_OnDeviceConnected_PopulatesDeviceConnectedTriggerKey()
    {
        var t = Single(Wrap("", """[OnDeviceConnected(Class = "USB", Pattern = "YubiKey*")]"""));
        t.TriggerKey.Should().Be(ComputerUseTriggerKeys.DeviceConnected);
        t.Parameters[ComputerUseTriggerKeys.DeviceClass].Should().Be("USB");
        t.Parameters[ComputerUseTriggerKeys.DeviceNamePattern].Should().Be("YubiKey*");
    }

    [Test]
    public void Parse_OnDeviceDisconnected_PopulatesDeviceDisconnectedTriggerKey()
    {
        Single(Wrap("", """[OnDeviceDisconnected(Class = "USB")]""")).TriggerKey
            .Should().Be(ComputerUseTriggerKeys.DeviceDisconnected);
    }

    // ── [OnQueryReturnsRows] ──────────────────────────────────────

    [Test]
    public void Parse_OnQueryReturnsRowsWithSelectCount_PopulatesQueryReturnsRowsTriggerKey()
    {
        Single(Wrap("", """[OnQueryReturnsRows("SELECT COUNT(*) FROM PendingItems WHERE Done = 0")]""")).TriggerKey
            .Should().Be(DatabaseAccessTriggerKeys.QueryReturnsRows);
    }

    [Test]
    public void Parse_OnQueryReturnsRowsWithNonCountQuery_EmitsTask431()
    {
        var result = TaskScriptEngine.Parse(Wrap("", """[OnQueryReturnsRows("SELECT * FROM PendingItems")]"""));
        result.Diagnostics.Should().Contain(d => d.Code == "TASK431");
    }

    [Test]
    public void Parse_OnQueryReturnsRowsWithPollInterval_ExtractsPollInterval()
    {
        var t = Single(Wrap("", """[OnQueryReturnsRows("SELECT COUNT(*) FROM Q", PollInterval = 30)]"""));
        t.Parameters[DatabaseAccessTriggerKeys.QueryPollIntervalSecs].Should().Be("30");
    }

    // ── [OnMetricThreshold] ───────────────────────────────────────

    [Test]
    public void Parse_OnMetricThreshold_PopulatesMetricFields()
    {
        var t = Single(Wrap("", """[OnMetricThreshold("System.CpuPercent", Threshold = 90.0, Direction = ThresholdDirection.Above)]"""));
        t.TriggerKey.Should().Be(MetricTriggerKeys.MetricThreshold);
        t.Parameters[MetricTriggerKeys.Source].Should().Be("System.CpuPercent");
        t.Parameters[MetricTriggerKeys.Threshold].Should().Be("90");
        t.Parameters[MetricTriggerKeys.Direction].Should().Be(ThresholdDirection.Above.ToString());
    }

    // ── [OnStartup] / [OnShutdown] ────────────────────────────────

    [Test]
    public void Parse_OnStartup_PopulatesStartupTriggerKey()
    {
        Single(Wrap("", """[OnStartup]""")).TriggerKey
            .Should().Be(TaskScriptingTriggerKeys.Startup);
    }

    [Test]
    public void Parse_OnShutdown_PopulatesShutdownTriggerKey()
    {
        Single(Wrap("", """[OnShutdown]""")).TriggerKey
            .Should().Be(TaskScriptingTriggerKeys.Shutdown);
    }

    // ── [OsShortcut] ──────────────────────────────────────────────

    [Test]
    public void Parse_OsShortcut_PopulatesShortcutFieldsWithOsShortcutTriggerKey()
    {
        var t = Single(Wrap("", """[OsShortcut("Run Ingest", Icon = "ingest.ico", Category = "Data")]"""));
        t.TriggerKey.Should().Be(OsShortcutTriggerKeys.OsShortcut);
        t.Parameters[OsShortcutTriggerKeys.ShortcutLabel].Should().Be("Run Ingest");
        t.Parameters[OsShortcutTriggerKeys.ShortcutIcon].Should().Be("ingest.ico");
        t.Parameters[OsShortcutTriggerKeys.ShortcutCategory].Should().Be("Data");
    }

    // ── [OnTrigger] ───────────────────────────────────────────────

    [Test]
    public void Parse_OnTrigger_PopulatesTriggerKey()
    {
        Single(Wrap("", """[OnTrigger("MyCustomSource")]""")).TriggerKey
            .Should().Be("MyCustomSource");
    }

    [Test]
    public void Parse_OnTriggerWithFilter_ExtractsFilter()
    {
        var t = Single(Wrap("", """[OnTrigger("MyCustomSource", Filter = "type=foo")]"""));
        t.Parameters[TaskScriptingTriggerKeys.CustomSourceFilter].Should().Be("type=foo");
    }

    // ── TASK428 — [WebhookSecret] without [OnWebhook] ─────────────

    [Test]
    public void Parse_WebhookSecretWithoutOnWebhook_EmitsTask428()
    {
        var result = TaskScriptEngine.Parse(Wrap("", """[WebhookSecret("MY_SECRET")]"""));
        result.Diagnostics.Should().Contain(d => d.Code == "TASK428");
    }

    [Test]
    public void Parse_WebhookSecretWithOnWebhook_NoTask428()
    {
        var result = TaskScriptEngine.Parse(Wrap("", """
[OnWebhook("/hook/deploy")]
[WebhookSecret("MY_SECRET")]
"""));
        result.Diagnostics.Should().NotContain(d => d.Code == "TASK428");
    }

    // ── Multiple triggers on same class ──────────────────────────

    [Test]
    public void Parse_MultipleTriggerAttributes_AllExtracted()
    {
        var source = Wrap("", """
[Schedule("0 * * * *")]
[OnEvent("ModelAdded")]
[OnStartup]
""");
        var def = ParseOk(source);
        def.TriggerDefinitions.Should().HaveCount(3);
        def.TriggerDefinitions.Select(t => t.TriggerKey)
            .Should().BeEquivalentTo([
                TaskScriptingTriggerKeys.Cron,
                AgentOrchestrationTriggerKeys.Event,
                TaskScriptingTriggerKeys.Startup,
            ]);
    }

    // ── Line numbers are captured ─────────────────────────────────

    [Test]
    public void Parse_TriggerDefinition_RecordsNonZeroLineNumber()
    {
        Single(Wrap("", """[Schedule("0 9 * * *")]""")).Line.Should().BeGreaterThan(0);
    }
}
