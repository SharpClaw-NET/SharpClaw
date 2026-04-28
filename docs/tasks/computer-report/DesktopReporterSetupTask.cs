[Task("desktop-reporter-setup")]
[Description("Creates or refreshes the demo desktop reporter agent, role, and channel used to report the current computer state.")]
[RequiresModule("sharpclaw_computer_use")]
public class DesktopReporterSetupTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Log("Preparing the desktop reporter demo resources.");

        var model = await FindModel("claude-sonnet-4.6");
        var role = await CreateRole("Desktop Reporter Demo Role");
        await SetRolePermissions(role, """
{"globalFlags":{"CanEnumerateWindows":5,"CanFocusWindow":5,"CanResizeWindow":5,"CanSendHotkey":5,"CanReadClipboard":5,"CanWriteClipboard":5,"CanClickDesktop":5,"CanTypeOnDesktop":5,"CanInvokeTasksAsTool":5},"resourceGrants":{"CuDisplay":[{"resourceId":"ffffffff-ffff-ffff-ffff-ffffffffffff","clearance":5}],"CuNativeApp":[{"resourceId":"ffffffff-ffff-ffff-ffff-ffffffffffff","clearance":5}]}}
""");

        var agent = await CreateAgent(
            "Desktop Reporter",
            model,
            "You are the Desktop Reporter demo agent. Use the Computer Use tools only for observation unless the user explicitly asks for interaction. Prefer safe enumeration, screenshots, clipboard reads, and native application inventory. Never modify user files or close windows while producing a report.",
            "demo.desktop-reporter.agent");
        await AssignRole(agent, role);

        var channel = await CreateChannel("Desktop Reporter", agent, "demo.desktop-reporter.channel");
        await AddAllowedAgent(agent, channel);

        await Log("Desktop reporter resources are ready. Existing resources with the same custom IDs were reused and refreshed.");
        await Emit("Desktop reporter setup ready. AgentId=" + agent + "; ChannelId=" + channel + "; RoleId=" + role);
    }
}

