# SharpClaw Module: Office Apps

> **Module ID:** `sharpclaw_office_apps`
> **Display Name:** Office Apps
> **Version:** 1.0.0
> **Tool Prefix:** `oa`
> **Platforms:** Windows, Linux, macOS
> **Exports:** none
> **Requires:** none

---

## Overview

The Office Apps module provides document session management, file-based
spreadsheet operations via ClosedXML (`.xlsx` / `.xlsm`) and CsvHelper
(`.csv`), and live Excel COM Interop for reading/writing workbooks that
are open in a running Excel instance (Windows only).

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `oa_`
when sent to the model — for example, `read_range` becomes
`oa_read_range`.

### Platform notes

| Feature | Windows | Linux | macOS |
|---------|---------|-------|-------|
| File-based spreadsheet (.xlsx, .xlsm, .csv) | ✅ | ✅ | ✅ |
| Live Excel COM Interop (`oa_live_read_range`, `oa_live_write_range`) | ✅ | ❌ | ❌ |

---

## Table of Contents

- [Tools](#tools)
  - [oa_register_document](#oa_register_document)
  - [oa_read_range](#oa_read_range)
  - [oa_write_range](#oa_write_range)
  - [oa_list_sheets](#oa_list_sheets)
  - [oa_create_sheet](#oa_create_sheet)
  - [oa_delete_sheet](#oa_delete_sheet)
  - [oa_get_info](#oa_get_info)
  - [oa_create_workbook](#oa_create_workbook)
  - [oa_live_read_range](#oa_live_read_range)
  - [oa_live_write_range](#oa_live_write_range)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Tools

### oa_register_document

Register an existing file as a document session. The `documentType` is
inferred from the file extension (`.xlsx`/`.xlsm` → `Spreadsheet`,
`.csv` → `Csv`). Returns a session ID that other tools use as
`targetId`.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `filePath` | string | yes | Absolute path to the document file |
| `name` | string | no | Display name (defaults to filename without extension) |
| `description` | string | no | Optional description |

**Permission:** Global — requires `canCreateDocumentSessions` flag.

**Returns:**

```json
{
  "sessionId": "guid",
  "name": "string",
  "filePath": "string",
  "documentType": "Spreadsheet | Csv"
}
```

---

### oa_read_range

Read cells from a registered document as a JSON grid (array of arrays).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | no | Sheet name (defaults to first/active sheet) |
| `range` | string | no | Cell range: `"A1:C10"`, `"A:A"`, or omit for entire sheet |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

**Returns:** JSON grid of cell values.

---

### oa_write_range

Write a JSON grid (array of arrays) or a single value to a range.
Strings starting with `=` are treated as formulas. CSV files are
rewritten atomically.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | no | Sheet name (defaults to first/active sheet) |
| `range` | string | yes | Starting cell or range (e.g. `"A1"`, `"B2:C10"`) |
| `data` | JSON | yes | Grid (array of arrays) or single value. `"="` prefix → formula |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_list_sheets

List all sheets in a workbook with row and column counts. CSV files
return a single synthetic sheet.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_create_sheet

Add a new sheet to an `.xlsx` / `.xlsm` workbook. **Not supported for
CSV.**

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | yes | Name of the new sheet |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_delete_sheet

Remove a sheet from an `.xlsx` / `.xlsm` workbook. **Not supported for
CSV.**

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | yes | Name of the sheet to remove |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_get_info

Get workbook metadata: sheets, named ranges, file size, last modified
timestamp.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_create_workbook

Create a new `.xlsx` or `.csv` file with optional initial data. A
document session is automatically registered for the new file.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `filePath` | string | yes | Absolute path for the new file (`.xlsx` or `.csv`) |
| `sheetName` | string | no | Initial sheet name (defaults to `Sheet1`) |
| `data` | JSON | no | Optional initial data as JSON grid |

**Permission:** Global — requires `canCreateDocumentSessions` flag.

**Returns:**

```json
{
  "sessionId": "guid",
  "name": "string",
  "filePath": "string",
  "documentType": "Spreadsheet | Csv",
  "createResult": "string"
}
```

---

### oa_live_read_range

Read cells from a workbook that is **currently open in Excel** via COM
Interop. **Windows only.** Use this when the file is open in Excel and
you want to read live (unsaved) data.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | no | Sheet name (defaults to active sheet) |
| `range` | string | no | Cell range or omit for entire sheet |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

### oa_live_write_range

Write to a workbook that is **currently open in Excel** via COM Interop.
**Windows only.** Changes appear immediately in the running Excel
instance.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | Document session GUID |
| `sheetName` | string | no | Sheet name (defaults to active sheet) |
| `range` | string | yes | Starting cell or range |
| `data` | JSON | yes | Grid (array of arrays) or single value |

**Permission:** Per-resource — requires `documentSessionAccesses` grant.

---

## CLI Commands

The module registers a top-level `docs` command (aliases: `office`,
`oa`):

```
docs list     List registered document sessions
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Document Sessions | All per-resource tools (`oa_read_range`, `oa_write_range`, `oa_list_sheets`, `oa_create_sheet`, `oa_delete_sheet`, `oa_get_info`, `oa_live_read_range`, `oa_live_write_range`) |

Document sessions are registered via `POST /resources/documentsessions`
or via the `oa_register_document` / `oa_create_workbook` tools.

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canCreateDocumentSessions` | `oa_register_document`, `oa_create_workbook` |

The `createDocumentSessionsClearance` override defaults to `Unset`
(uses the role's `defaultClearance`).

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `documentSessionAccesses` | DocumentSessions | `oa_read_range`, `oa_write_range`, `oa_list_sheets`, `oa_create_sheet`, `oa_delete_sheet`, `oa_get_info`, `oa_live_read_range`, `oa_live_write_range` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_office_apps",
  "displayName": "Office Apps",
  "version": "1.0.0",
  "toolPrefix": "oa",
  "entryAssembly": "SharpClaw.Modules.OfficeApps",
  "platforms": ["windows", "linux", "macos"],
  "exports": [],
  "requires": []
}
```
