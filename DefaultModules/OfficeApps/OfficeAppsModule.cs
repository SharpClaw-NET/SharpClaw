using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Documents;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Modules.OfficeApps;

/// <summary>
/// Default module: document session management, file-based spreadsheet
/// operations (ClosedXML / CsvHelper), and live Excel COM Interop.
/// </summary>
public sealed class OfficeAppsModule : ISharpClawModule
{
    public string Id => "sharpclaw_office_apps";
    public string DisplayName => "Office Apps";
    public string ToolPrefix => "oa";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SpreadsheetService>();
        services.AddSingleton<ExcelComInteropService>();
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "docs",
            Aliases: ["office", "oa"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Office Apps module commands",
            UsageLines:
            [
                "docs list                      List registered document sessions",
            ],
            Handler: HandleDocsCommandAsync),
    ];

    private static readonly JsonSerializerOptions CliJsonPrint = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task HandleDocsCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            PrintDocsUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "list":
            {
                var svc = sp.GetRequiredService<DocumentSessionService>();
                var list = await svc.ListAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(list, CliJsonPrint));
                break;
            }
            default:
                Console.Error.WriteLine($"Unknown docs command: {sub}");
                PrintDocsUsage();
                break;
        }
    }

    private static void PrintDocsUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  docs list     List registered document sessions");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var docSession = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "AccessDocumentSessionAsync");
        var createDoc = new ModuleToolPermission(
            IsPerResource: false, Check: null, DelegateTo: "CreateDocumentSessionAsync");

        return
        [
            new("register_document",
                "Register a file as a document session. Auto-detects type from extension (.xlsx/.xlsm → Spreadsheet, .csv → Csv). Returns session ID for use with spreadsheet tools.",
                RegisterDocumentSchema(), createDoc),
            new("read_range",
                "Read cells from a registered document as JSON grid. Supports A1:C10 notation, whole column (A:A), or omit range for entire sheet. Works on .xlsx, .xlsm, .csv.",
                ReadRangeSchema(), docSession),
            new("write_range",
                "Write JSON grid or single value to a range in a registered document. Supports formulas (strings starting with '='). CSV files are rewritten atomically.",
                WriteRangeSchema(), docSession),
            new("list_sheets",
                "List all sheets with row/column counts. CSV returns single sheet.",
                ResourceOnlySchema(), docSession),
            new("create_sheet",
                "Add a new sheet to an .xlsx/.xlsm workbook. Not supported for CSV.",
                SheetNameSchema(), docSession),
            new("delete_sheet",
                "Remove a sheet from an .xlsx/.xlsm workbook. Not supported for CSV.",
                SheetNameSchema(), docSession),
            new("get_info",
                "Workbook metadata: sheets, named ranges, file size, last modified.",
                ResourceOnlySchema(), docSession),
            new("create_workbook",
                "Create a new .xlsx or .csv file with optional initial data. Auto-registers a document session.",
                CreateWorkbookSchema(), createDoc),
            new("live_read_range",
                "Read cells from a workbook currently open in Excel (COM Interop, Windows only). Use when the file is open in Excel and you want to read live data.",
                ReadRangeSchema(), docSession),
            new("live_write_range",
                "Write to a workbook currently open in Excel (COM Interop, Windows only). Use when you need changes to appear immediately in the running Excel instance.",
                WriteRangeSchema(), docSession),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        var spreadsheet = sp.GetRequiredService<SpreadsheetService>();
        var excel = sp.GetRequiredService<ExcelComInteropService>();

        return toolName switch
        {
            "register_document" => await RegisterDocumentAsync(parameters, sp, ct),
            "read_range" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "write_range" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "list_sheets" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "create_sheet" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "delete_sheet" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "get_info" => await SpreadsheetActionAsync(toolName, parameters, job, spreadsheet, sp, ct),
            "create_workbook" => await CreateWorkbookAsync(parameters, spreadsheet, sp, ct),
            "live_read_range" => await LiveSpreadsheetActionAsync(toolName, parameters, job, excel, sp, ct),
            "live_write_range" => await LiveSpreadsheetActionAsync(toolName, parameters, job, excel, sp, ct),
            _ => throw new InvalidOperationException($"Unknown office-apps tool: {toolName}"),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool implementations
    // ═══════════════════════════════════════════════════════════════

    private static async Task<string> RegisterDocumentAsync(
        JsonElement parameters, IServiceProvider sp, CancellationToken ct)
    {
        var filePath = Str(parameters, "filePath")
            ?? throw new InvalidOperationException(
                "register_document requires a 'filePath' field.");

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"File not found: {fullPath}");

        var docService = sp.GetRequiredService<DocumentSessionService>();
        var docSession = await docService.CreateAsync(
            new CreateDocumentSessionRequest(
                fullPath,
                Str(parameters, "name") ?? Path.GetFileNameWithoutExtension(fullPath),
                Str(parameters, "description")),
            ct);

        return JsonSerializer.Serialize(new
        {
            sessionId = docSession.Id,
            name = docSession.Name,
            filePath = docSession.FilePath,
            documentType = docSession.DocumentType.ToString(),
        });
    }

    private static async Task<string> SpreadsheetActionAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        SpreadsheetService spreadsheet, IServiceProvider sp, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                $"{toolName} requires a ResourceId (DocumentSession).");

        var db = sp.GetRequiredService<SharpClawDbContext>();
        var docSession = await db.DocumentSessions.FirstOrDefaultAsync(
            d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException(
                $"Document session {job.ResourceId} not found.");

        var sheetName = Str(parameters, "sheetName");
        var range = Str(parameters, "range");

        return toolName switch
        {
            "read_range" => spreadsheet.ReadRange(docSession.FilePath, sheetName, range),
            "write_range" => spreadsheet.WriteRange(
                docSession.FilePath, sheetName, range, ExtractJsonData(parameters)),
            "list_sheets" => spreadsheet.ListSheets(docSession.FilePath),
            "create_sheet" => spreadsheet.CreateSheet(
                docSession.FilePath,
                sheetName ?? throw new InvalidOperationException(
                    "create_sheet requires a 'sheetName' parameter.")),
            "delete_sheet" => spreadsheet.DeleteSheet(
                docSession.FilePath,
                sheetName ?? throw new InvalidOperationException(
                    "delete_sheet requires a 'sheetName' parameter.")),
            "get_info" => spreadsheet.GetInfo(docSession.FilePath),
            _ => throw new InvalidOperationException($"Unexpected spreadsheet action: {toolName}"),
        };
    }

    private static async Task<string> CreateWorkbookAsync(
        JsonElement parameters, SpreadsheetService spreadsheet,
        IServiceProvider sp, CancellationToken ct)
    {
        var filePath = Str(parameters, "filePath")
            ?? throw new InvalidOperationException(
                "create_workbook requires a 'filePath' parameter.");

        var fullPath = Path.GetFullPath(filePath);
        var sheetName = Str(parameters, "sheetName");

        var result = spreadsheet.CreateWorkbook(
            fullPath, sheetName, ExtractJsonDataOrNull(parameters));

        // Auto-register a DocumentSession for the new workbook
        var docService = sp.GetRequiredService<DocumentSessionService>();
        var docSession = await docService.CreateAsync(
            new CreateDocumentSessionRequest(
                fullPath,
                Path.GetFileNameWithoutExtension(fullPath)),
            ct);

        return JsonSerializer.Serialize(new
        {
            sessionId = docSession.Id,
            name = docSession.Name,
            filePath = docSession.FilePath,
            documentType = docSession.DocumentType.ToString(),
            createResult = result,
        });
    }

    private static async Task<string> LiveSpreadsheetActionAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        ExcelComInteropService excel, IServiceProvider sp, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                $"{toolName} requires a ResourceId (DocumentSession).");

        var db = sp.GetRequiredService<SharpClawDbContext>();
        var docSession = await db.DocumentSessions.FirstOrDefaultAsync(
            d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException(
                $"Document session {job.ResourceId} not found.");

        var sheetName = Str(parameters, "sheetName");
        var range = Str(parameters, "range");

        return toolName switch
        {
            "live_read_range" => excel.ReadRange(docSession.FilePath, sheetName, range),
            "live_write_range" => excel.WriteRange(
                docSession.FilePath, sheetName, range, ExtractJsonData(parameters)),
            _ => throw new InvalidOperationException(
                $"Unexpected live spreadsheet action: {toolName}"),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON parameter helpers
    // ═══════════════════════════════════════════════════════════════

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement ExtractJsonData(JsonElement parameters)
    {
        if (parameters.TryGetProperty("data", out var data))
            return data;

        throw new InvalidOperationException(
            "Spreadsheet write requires a 'data' parameter.");
    }

    private static JsonElement? ExtractJsonDataOrNull(JsonElement parameters) =>
        parameters.TryGetProperty("data", out var data) ? data : null;

    // ═══════════════════════════════════════════════════════════════
    // JSON schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement RegisterDocumentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "filePath": {
                        "type": "string",
                        "description": "Absolute path to the document file."
                    },
                    "name": {
                        "type": "string",
                        "description": "Display name (optional, defaults to file name)."
                    },
                    "description": {
                        "type": "string",
                        "description": "Optional description."
                    }
                },
                "required": ["filePath"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ReadRangeSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Document session GUID."
                    },
                    "sheetName": {
                        "type": "string",
                        "description": "Sheet name (optional, defaults to first/active sheet)."
                    },
                    "range": {
                        "type": "string",
                        "description": "Cell range (A1:C10, A:A, or omit for entire sheet)."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement WriteRangeSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Document session GUID."
                    },
                    "sheetName": {
                        "type": "string",
                        "description": "Sheet name (optional, defaults to first/active sheet)."
                    },
                    "range": {
                        "type": "string",
                        "description": "Starting cell or range (e.g. A1, B2:C10)."
                    },
                    "data": {
                        "description": "JSON grid (array of arrays) or single value. Strings starting with '=' are formulas."
                    }
                },
                "required": ["targetId", "range", "data"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement SheetNameSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Document session GUID."
                    },
                    "sheetName": {
                        "type": "string",
                        "description": "Name of the sheet."
                    }
                },
                "required": ["targetId", "sheetName"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateWorkbookSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "filePath": {
                        "type": "string",
                        "description": "Absolute path for the new file (.xlsx or .csv)."
                    },
                    "sheetName": {
                        "type": "string",
                        "description": "Initial sheet name (optional, defaults to Sheet1)."
                    },
                    "data": {
                        "description": "Optional initial data as JSON grid."
                    }
                },
                "required": ["filePath"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Resource GUID."
                    }
                },
                "required": ["targetId"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
