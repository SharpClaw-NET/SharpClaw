SharpClaw Module: Office Apps — Agent Skill Reference

Module ID: sharpclaw_office_apps
Display Name: Office Apps
Tool Prefix: oa
Version: 1.0.0
Platforms: Windows, Linux, macOS
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_office_apps
Default: disabled
Prerequisites: none
Platform: Windows, Linux, macOS

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_office_apps": "true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_office_apps
  module enable sharpclaw_office_apps

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Document session management, file-based spreadsheet operations
(ClosedXML / CsvHelper), and live Excel COM Interop (Windows only).
Supports .xlsx, .xlsm, and .csv files.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "oa_" when sent to the model.

────────────────────────────────────────
TOOLS (10)
────────────────────────────────────────

oa_register_document
  Register a file as a document session. Auto-detects type from extension
  (.xlsx/.xlsm → Spreadsheet, .csv → Csv). Returns session ID for use
  with spreadsheet tools.
  Params: filePath (string, required), name (string, optional),
          description (string, optional)
  Permission: global (CreateDocumentSession)

oa_read_range
  Read cells from a registered document as JSON grid. Supports A1:C10
  notation, whole column (A:A), or omit range for entire sheet.
  Works on .xlsx, .xlsm, .csv.
  Params: targetId (document session GUID, required),
          sheetName (string, optional — defaults to first/active sheet),
          range (string, optional — e.g. "A1:C10", "A:A", omit for all)
  Permission: per-resource (DocumentSession)

oa_write_range
  Write JSON grid or single value to a range in a registered document.
  Supports formulas (strings starting with "="). CSV files are rewritten
  atomically.
  Params: targetId (document session GUID, required),
          sheetName (string, optional), range (string, required),
          data (JSON grid or single value, required)
  Permission: per-resource (DocumentSession)

oa_list_sheets
  List all sheets with row/column counts. CSV returns single sheet.
  Params: targetId (document session GUID, required)
  Permission: per-resource (DocumentSession)

oa_create_sheet
  Add a new sheet to an .xlsx/.xlsm workbook. Not supported for CSV.
  Params: targetId (document session GUID, required),
          sheetName (string, required)
  Permission: per-resource (DocumentSession)

oa_delete_sheet
  Remove a sheet from an .xlsx/.xlsm workbook. Not supported for CSV.
  Params: targetId (document session GUID, required),
          sheetName (string, required)
  Permission: per-resource (DocumentSession)

oa_get_info
  Workbook metadata: sheets, named ranges, file size, last modified.
  Params: targetId (document session GUID, required)
  Permission: per-resource (DocumentSession)

oa_create_workbook
  Create a new .xlsx or .csv file with optional initial data.
  Auto-registers a document session.
  Params: filePath (string, required), sheetName (string, optional),
          data (JSON grid, optional)
  Permission: global (CreateDocumentSession)

oa_live_read_range
  Read cells from a workbook currently open in Excel (COM Interop,
  Windows only). Use when the file is open in Excel and you want to
  read live data.
  Params: targetId (document session GUID, required),
          sheetName (string, optional), range (string, optional)
  Permission: per-resource (DocumentSession)

oa_live_write_range
  Write to a workbook currently open in Excel (COM Interop, Windows only).
  Use when you need changes to appear immediately in the running Excel
  instance.
  Params: targetId (document session GUID, required),
          sheetName (string, optional), range (string, required),
          data (JSON grid or single value, required)
  Permission: per-resource (DocumentSession)

────────────────────────────────────────
CLI
────────────────────────────────────────
docs list                 List registered document sessions

Aliases: office, oa

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- DocumentSessions — for all per-resource tools (read_range, write_range,
  list_sheets, create_sheet, delete_sheet, get_info, live_read_range,
  live_write_range)

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canCreateDocumentSessions
Clearance overrides: createDocumentSessionsClearance
Per-resource: documentSessionAccesses
