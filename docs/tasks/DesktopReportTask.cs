[Task("desktop-report")]
[Description("Runs the demo desktop reporter agent against the Computer Use module and returns a standardized report about the current computer.")]
[RequiresModule("sharpclaw_computer_use")]
public class DesktopReportTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Log("Starting desktop report demo run.");

        var reporter = await FindAgent("demo.desktop-reporter.agent");
        var thread = await CreateThread("current", "Desktop report run");

        var report = await ChatToThread(
            thread,
            "You are producing a standardized desktop report for demo purposes. " +
            "Use as many safe Computer Use module tools as are relevant before answering. " +
            "Prioritize cu_enumerate_windows, cu_capture_display, cu_capture_window, cu_read_clipboard, and inventory-oriented native application data. " +
            "Do not close windows, stop processes, type, click, paste, launch new applications, or resize/focus windows unless a read-only data-gathering step absolutely requires it. " +
            "Clipboard reads are allowed for this demo, but summarize sensitive contents instead of repeating secrets. " +
            "Screenshots are allowed for this demo; use them as evidence without transcribing private secrets. " +
            "Gather enough evidence to answer these questions: What OS is this? What programs appear to be installed or running? What windows are open? What displays are attached? What is the timezone and current local time? What user/session clues are visible? What development tools are present? What security or privacy-sensitive observations should be noted? " +
            "Return only Markdown in exactly this format:\n" +
            "# Desktop Report\n" +
            "## Summary\n" +
            "- Computer identity:\n" +
            "- OS:\n" +
            "- Timezone and local time:\n" +
            "- Primary user/session clues:\n" +
            "## Displays\n" +
            "| Display | Resolution | Notes |\n" +
            "|---|---:|---|\n" +
            "## Open Windows\n" +
            "| Process | Title | PID | Evidence |\n" +
            "|---|---|---:|---|\n" +
            "## Installed or Available Programs\n" +
            "| Program | Evidence source | Confidence |\n" +
            "|---|---|---|\n" +
            "## Development Environment\n" +
            "- IDEs/editors:\n" +
            "- Terminals/shells:\n" +
            "- Runtimes/tools inferred:\n" +
            "## Clipboard\n" +
            "- Status:\n" +
            "- Safe summary:\n" +
            "## Notable Files, Apps, or Work Context\n" +
            "- \n" +
            "## Security and Privacy Notes\n" +
            "- \n" +
            "## Evidence Log\n" +
            "List each Computer Use tool call attempted and summarize the returned evidence.\n" +
            "## Gaps and Follow-up Questions\n" +
            "- What could not be determined?\n" +
            "- What should a human confirm?",
            reporter);

        await Emit(report);
        await Log("Desktop report demo run completed.");
    }
}

