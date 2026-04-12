SharpClaw Module: Computer Use — Agent Skill Reference

Module ID: sharpclaw_computer_use
Display Name: Computer Use
Tool Prefix: cu
Version: 1.0.0
Platforms: Windows only
Exports: window_management, desktop_input
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_computer_use
Default: disabled
Prerequisites: none
Platform: Windows only

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_computer_use": "true"

To disable, set to "false" or remove the key (missing = disabled).

Exports: window_management (IWindowManager), desktop_input (IDesktopInput).
These contracts are consumed optionally by Module Dev.

Runtime toggle (no restart required):
  module disable sharpclaw_computer_use
  module enable sharpclaw_computer_use

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Desktop awareness, window management, input simulation, clipboard access,
display capture, and process control. All tools are Windows-only.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "cu_" when sent to the model.

────────────────────────────────────────
TOOLS (13)
────────────────────────────────────────

cu_capture_display
  Screenshot a display; base64 PNG (vision) or text fallback.
  Params: targetId (display GUID, required)
  Permission: per-resource (DisplayDevice)

cu_click_desktop
  Click (x,y) on display. Returns screenshot.
  Params: targetId (display GUID, required), x (int, required), y (int, required),
          button ("left"|"right"|"middle", default "left"),
          clickType ("single"|"double", default "single")
  Permission: per-resource (DisplayDevice)

cu_type_on_desktop
  Type text; optional (x,y) to focus first. Returns screenshot.
  Params: targetId (display GUID, required), text (string, required),
          x (int, optional), y (int, optional)
  Permission: per-resource (DisplayDevice)

cu_enumerate_windows
  List visible desktop windows across all displays. Returns JSON array
  with title, processName, processId, executablePath.
  Params: none
  Permission: global (EnumerateWindows)

cu_launch_application
  Start a registered native application. Optionally open a file with it.
  Returns PID and window title.
  Params: targetId (native app GUID or resolved from alias),
          alias (string, optional), arguments (string, optional),
          filePath (string, optional)
  Permission: per-resource (NativeApplication)

cu_focus_window
  Bring window to foreground by PID, process name, or title substring.
  Params: processId (int, optional), processName (string, optional),
          titleContains (string, optional)
  Permission: global (EnumerateWindows)

cu_close_window
  Send graceful close (WM_CLOSE) to a window. App may prompt to save.
  Params: processId (int, optional), processName (string, optional),
          titleContains (string, optional)
  Permission: global (EnumerateWindows)

cu_resize_window
  Move/resize/minimize/maximize a window.
  Params: processId (int, optional), titleContains (string, optional),
          x (int, optional), y (int, optional),
          width (int, optional), height (int, optional),
          state (string, optional — "normal"|"minimized"|"maximized")
  Permission: global (EnumerateWindows)

cu_send_hotkey
  Send keyboard shortcut (e.g. "ctrl+s", "alt+tab"). Optional focus-first by PID/title.
  Params: keys (string, required), processId (int, optional),
          titleContains (string, optional)
  Permission: global (EnumerateWindows)

cu_capture_window
  Screenshot a single window by PID/title. Smaller than capture_display.
  Returns base64 PNG (vision) or dims.
  Params: processId (int, optional), processName (string, optional),
          titleContains (string, optional)
  Permission: global (EnumerateWindows)

cu_read_clipboard
  Read clipboard: text, file list, or image. Auto-detect or specify format.
  Params: format (string, optional)
  Permission: global (EnumerateWindows)

cu_write_clipboard
  Set clipboard to text or file paths. Pair with cu_send_hotkey("ctrl+v") for paste.
  Params: text (string, optional), filePaths (string[], optional)
  Permission: global (EnumerateWindows)

cu_stop_process
  Stop a process launched via cu_launch_application. Must match a registered native app.
  Params: processId (int, required), force (bool, optional, default false),
          alias (string, optional)
  Permission: per-resource (NativeApplication)

────────────────────────────────────────
CLI
────────────────────────────────────────
cu windows                List visible desktop windows
cu displays               List registered display devices
cu apps                   List registered native applications

Aliases: computer-use

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- DisplayDevices — for capture_display, click_desktop, type_on_desktop
- NativeApplications — for launch_application, stop_process

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canEnumerateWindows, canFocusWindow, canCloseWindow,
  canResizeWindow, canSendHotkey, canReadClipboard, canWriteClipboard,
  canClickDesktop, canTypeOnDesktop
Clearance overrides: enumerateWindowsClearance, focusWindowClearance,
  closeWindowClearance, resizeWindowClearance, sendHotkeyClearance,
  readClipboardClearance, writeClipboardClearance
Per-resource: displayDeviceAccesses, nativeApplicationAccesses

────────────────────────────────────────
EXPORTED CONTRACTS
────────────────────────────────────────
window_management  → IWindowManager
  Window enumeration, focus, capture, close.
desktop_input      → IDesktopInput
  Mouse click, keyboard input, hotkey simulation.
