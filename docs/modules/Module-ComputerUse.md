# SharpClaw Module: Computer Use

> **Module ID:** `sharpclaw_computer_use`
> **Display Name:** Computer Use
> **Version:** 1.0.0
> **Tool Prefix:** `cu`
> **Platforms:** Windows only
> **Exports:** `window_management` (`IWindowManager`), `desktop_input` (`IDesktopInput`)

---

## Overview

The Computer Use module provides desktop awareness, window management,
input simulation (mouse clicks, keyboard typing, hotkeys), clipboard
access, display capture, and process control. All tools in this module
are **Windows only** and use Win32 APIs under the hood.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `cu_`
when sent to the model — for example, `capture_display` becomes
`cu_capture_display`.

---

## Table of Contents

- [Tools](#tools)
  - [cu_capture_display](#cu_capture_display)
  - [cu_click_desktop](#cu_click_desktop)
  - [cu_type_on_desktop](#cu_type_on_desktop)
  - [cu_enumerate_windows](#cu_enumerate_windows)
  - [cu_launch_application](#cu_launch_application)
  - [cu_focus_window](#cu_focus_window)
  - [cu_close_window](#cu_close_window)
  - [cu_resize_window](#cu_resize_window)
  - [cu_send_hotkey](#cu_send_hotkey)
  - [cu_capture_window](#cu_capture_window)
  - [cu_read_clipboard](#cu_read_clipboard)
  - [cu_write_clipboard](#cu_write_clipboard)
  - [cu_stop_process](#cu_stop_process)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Exported Contracts](#exported-contracts)
- [Module Manifest](#module-manifest)

---

## Tools

### cu_capture_display

Screenshot a display; returns base64 PNG for vision-capable models or a
text fallback (dimensions) for non-vision models.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Display device resource GUID |

**Permission:** Per-resource — requires `displayDeviceAccesses` grant
for the target display device.

**Returns:** Screenshot description with `[SCREENSHOT_BASE64]` payload.

---

### cu_click_desktop

Click at absolute `(x, y)` coordinates on a display. Returns a
screenshot with a click marker.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Display device GUID |
| `x` | integer | yes | X coordinate relative to the display |
| `y` | integer | yes | Y coordinate relative to the display |
| `button` | string | no | `"left"` (default), `"right"`, `"middle"` |
| `clickType` | string | no | `"single"` (default), `"double"` |

**Permission:** Per-resource — requires `canClickDesktop` or
`displayDeviceAccesses` grant.

**Returns:** Click confirmation with `[SCREENSHOT_BASE64]` payload.

---

### cu_type_on_desktop

Type text on the focused element. Optionally click `(x, y)` first to
set focus. Returns a screenshot after typing.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Display device GUID |
| `text` | string | yes | Text to type |
| `x` | integer | no | X coordinate for focus click |
| `y` | integer | no | Y coordinate for focus click |

**Permission:** Per-resource — requires `canTypeOnDesktop` or
`displayDeviceAccesses` grant.

**Returns:** Typing confirmation with `[SCREENSHOT_BASE64]` payload.

---

### cu_enumerate_windows

List all visible desktop windows across all displays.

**Parameters:** none (empty object)

**Permission:** Global — requires `canEnumerateWindows` flag.

**Returns:** JSON array of window objects:

```json
[
  {
    "title": "Program.cs - SharpClaw - Visual Studio",
    "processName": "devenv",
    "processId": 12345,
    "executablePath": "C:\\Program Files\\Microsoft Visual Studio\\..."
  }
]
```

---

### cu_launch_application

Start a registered native application. Optionally open a specific file.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | no | Native application resource GUID (or resolved from defaults) |
| `alias` | string | no | Short alias for the native application |
| `arguments` | string | no | Command-line arguments |
| `filePath` | string | no | File to open with the application |

Either `targetId` or `alias` must identify a registered native
application.

**Permission:** Per-resource — requires `nativeApplicationAccesses`
grant.

**Returns:** PID and window title of the launched process.

---

### cu_focus_window

Bring a window to the foreground. Identify the window by PID, process
name, or title substring.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `processId` | integer | no | Target process ID |
| `processName` | string | no | Target process name |
| `titleContains` | string | no | Window title substring |

At least one identifier must be provided.

**Permission:** Global — requires `canFocusWindow` flag.

---

### cu_close_window

Send a graceful close (WM_CLOSE) to a window. The application may
prompt to save unsaved work.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `processId` | integer | no | Target process ID |
| `processName` | string | no | Target process name |
| `titleContains` | string | no | Window title substring |

**Permission:** Global — requires `canCloseWindow` flag.

---

### cu_resize_window

Move, resize, minimize, or maximize a window.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `processId` | integer | no | Target process ID |
| `titleContains` | string | no | Window title substring |
| `x` | integer | no | New X position |
| `y` | integer | no | New Y position |
| `width` | integer | no | New width |
| `height` | integer | no | New height |
| `state` | string | no | `"normal"`, `"minimized"`, `"maximized"` |

**Permission:** Global — requires `canResizeWindow` flag.

---

### cu_send_hotkey

Send a keyboard shortcut (e.g. `"ctrl+s"`, `"alt+tab"`,
`"ctrl+shift+p"`). Optionally focus a window first by PID or title.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `keys` | string | yes | Hotkey string (e.g. `"ctrl+s"`) |
| `processId` | integer | no | Focus this PID first |
| `titleContains` | string | no | Focus window with this title first |

**Permission:** Global — requires `canSendHotkey` flag.

---

### cu_capture_window

Screenshot a single window by PID, process name, or title. Smaller and
faster than `cu_capture_display`. Returns base64 PNG for vision models
or dimensions for non-vision.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `processId` | integer | no | Target process ID |
| `processName` | string | no | Target process name |
| `titleContains` | string | no | Window title substring |

**Permission:** Global — requires `canEnumerateWindows` flag.

---

### cu_read_clipboard

Read clipboard contents. Supports text, file lists, and images.
Auto-detects format unless explicitly specified.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `format` | string | no | Force format: `"text"`, `"files"`, `"image"` |

**Permission:** Global — requires `canReadClipboard` flag.

---

### cu_write_clipboard

Set the clipboard to text or file paths. Pair with
`cu_send_hotkey("ctrl+v")` to paste into an application.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `text` | string | no | Text to set on clipboard |
| `filePaths` | string[] | no | File paths to set on clipboard |

Provide either `text` or `filePaths`.

**Permission:** Global — requires `canWriteClipboard` flag.

---

### cu_stop_process

Stop a process that was launched via `cu_launch_application`. The target
must match a registered native application (by resource ID or alias).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `processId` | integer | yes | PID of the process to stop |
| `force` | boolean | no | Force kill (default `false`) |
| `alias` | string | no | Native application alias |

**Permission:** Per-resource — requires `nativeApplicationAccesses`
grant.

---

## CLI Commands

The module registers a top-level `cu` command (alias: `computer-use`):

```
cu windows    List visible desktop windows
cu displays   List registered display devices
cu apps       List registered native applications
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Display Devices | `cu_capture_display`, `cu_click_desktop`, `cu_type_on_desktop` |
| Native Applications | `cu_launch_application`, `cu_stop_process` |

Display devices can be synced via `POST /resources/displaydevices/sync`.
Native applications are registered via `POST /resources/nativeapplications`.

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canEnumerateWindows` | `cu_enumerate_windows`, `cu_focus_window`, `cu_close_window`, `cu_capture_window` |
| `canFocusWindow` | `cu_focus_window` |
| `canCloseWindow` | `cu_close_window` |
| `canResizeWindow` | `cu_resize_window` |
| `canSendHotkey` | `cu_send_hotkey` |
| `canClickDesktop` | `cu_click_desktop` |
| `canTypeOnDesktop` | `cu_type_on_desktop` |
| `canReadClipboard` | `cu_read_clipboard` |
| `canWriteClipboard` | `cu_write_clipboard` |

Each global flag also has a corresponding clearance override
(e.g. `enumerateWindowsClearance`, `focusWindowClearance`) that
defaults to `Unset` (uses the role's `defaultClearance`).

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `displayDeviceAccesses` | DisplayDevices | `cu_capture_display`, `cu_click_desktop`, `cu_type_on_desktop` |
| `nativeApplicationAccesses` | NativeApplications | `cu_launch_application`, `cu_stop_process` |

---

## Exported Contracts

| Contract Name | Interface | Description |
|---------------|-----------|-------------|
| `window_management` | `IWindowManager` | Window enumeration, focus, capture, close |
| `desktop_input` | `IDesktopInput` | Mouse click, keyboard input, hotkey simulation |

Other modules can declare `"requires": ["window_management"]` in their
`module.json` to depend on the Computer Use module's window management
services.

---

## Module Manifest

```json
{
  "id": "sharpclaw_computer_use",
  "displayName": "Computer Use",
  "version": "1.0.0",
  "toolPrefix": "cu",
  "entryAssembly": "SharpClaw.Modules.ComputerUse",
  "platforms": ["windows"],
  "exports": [
    {
      "contractName": "window_management",
      "serviceType": "SharpClaw.Contracts.Modules.Contracts.IWindowManager"
    },
    {
      "contractName": "desktop_input",
      "serviceType": "SharpClaw.Contracts.Modules.Contracts.IDesktopInput"
    }
  ],
  "requires": []
}
```
